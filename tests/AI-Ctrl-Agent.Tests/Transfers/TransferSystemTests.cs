using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCtrl.Client.Link;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Transfers;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Security;
using AiCtrl.Tests.Server;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Transfers;

public sealed class TransferSystemTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly CookieContainer _cookies = new();

    private readonly HttpClientHandler _handler;

    private readonly HttpClient _http;

    private TestServerHost _host = null!;

    private TransferSystem _transfers = null!;

    private ClientDb _db = null!;

    private WorkspaceRegistry _workspaces = null!;

    private string _workspaceRoot = string.Empty;

    private string _tempDirectory = string.Empty;

    public TransferSystemTests()
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        _http = new HttpClient(_handler);
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _host = await TestServerHost.StartAsync(_directory);
        _http.BaseAddress = new Uri(_host.BaseUrl);
        using var login = await _http.PostAsync(
            "/test-login?email=test%40example.com&name=Test+User",
            null,
            ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        _db = new ClientDb(Path.Combine(_directory, "client.db"));
        await _db.InitializeAsync(ct);
        _workspaceRoot = Path.Combine(_directory, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        var workspace = new WorkspaceDefinition(
            "test",
            _workspaceRoot,
            AllowUpload: true,
            AllowDownload: true,
            AllowMkdir: true,
            AllowDelete: false,
            DenyPatterns: []);
        _workspaces = new WorkspaceRegistry(new WorkspaceOptions([workspace], []));
        _tempDirectory = Path.Combine(_directory, "transfers");
        var token = _host.Services.GetRequiredService<AgentTokenService>().Create("ma_test", out _);
        var link = new TransferLinkState();
        link.Set(new ServerApiClient(_host.BaseUrl, _http), token);
        _transfers = new TransferSystem(
            _db,
            _workspaces,
            link,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TransferSystem>.Instance,
            _tempDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        _handler.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Receive_downloads_verifies_and_resumes_partial_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("hello resumed transfer");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var workspaceId = _workspaces.Entries[0].WorkspaceId;
        var transferId = await CreateUploadAsync(workspaceId, content, sha, ct);
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(_tempDirectory, transferId + ".part"),
            content[..4],
            ct);

        var result = await _transfers.ReceiveAsync(
            new FilesReceivePayload(transferId, workspaceId, string.Empty, "note.txt", "fail", sha),
            ct);

        result.RelPath.ShouldBe("note.txt");
        var saved = await File.ReadAllBytesAsync(Path.Combine(_workspaceRoot, "note.txt"), ct);
        saved.ShouldBe(content);
        File.Exists(Path.Combine(_tempDirectory, transferId + ".part")).ShouldBeFalse();

        using var status = await _http.GetAsync($"/api/v1/machines/ma_test/transfers/{transferId}", ct);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
        statusBody.RootElement.GetProperty("data").GetProperty("status").GetString().ShouldBe("completed");
    }

    [Fact]
    public async Task Resume_completes_pending_receive_record()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("resume from the client database");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var workspaceId = _workspaces.Entries[0].WorkspaceId;
        var transferId = await CreateUploadAsync(workspaceId, content, sha, ct);
        await _db.UpsertTransferAsync(
            new ClientTransferRecord(
                transferId,
                TransferDirections.Upload,
                workspaceId,
                "resumed.txt",
                content.Length,
                sha,
                "[]",
                TransferStatuses.Uploading),
            ct);

        await _transfers.ResumeAsync(ct);

        var saved = await File.ReadAllBytesAsync(Path.Combine(_workspaceRoot, "resumed.txt"), ct);
        saved.ShouldBe(content);
        (await _db.GetTransferAsync(transferId, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Send_uploads_file_and_completes_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("data from the client");
        await File.WriteAllBytesAsync(Path.Combine(_workspaceRoot, "out.txt"), content, ct);
        var workspaceId = _workspaces.Entries[0].WorkspaceId;

        using var create = await _http.PostAsJsonAsync(
            $"/api/v1/machines/ma_test/workspaces/{workspaceId}/transfers/downloads",
            new { relPath = "out.txt", asZip = false },
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();
        transferId.ShouldNotBeNullOrWhiteSpace();

        await _transfers.SendAsync(
            new FilesSendPayload(transferId!, workspaceId, "out.txt", AsZip: false),
            ct);

        var served = await _http.GetByteArrayAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/content",
            ct);
        served.ShouldBe(content);
    }

    private async Task<string> CreateUploadAsync(
        string workspaceId,
        byte[] content,
        string sha,
        CancellationToken ct)
    {
        using var create = await _http.PostAsJsonAsync(
            $"/api/v1/machines/ma_test/workspaces/{workspaceId}/transfers/uploads",
            new
            {
                relDir = "",
                fileName = "note.txt",
                size = content.Length,
                sha256 = sha,
                conflict = "fail",
            },
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();
        transferId.ShouldNotBeNullOrWhiteSpace();

        using var chunk = new ByteArrayContent(content);
        using var put = await _http.PutAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/chunks/0",
            chunk,
            ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var complete = await _http.PostAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/complete",
            null,
            ct);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        return transferId!;
    }
}
