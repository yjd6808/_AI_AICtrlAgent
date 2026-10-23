using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AiCtrl.Client.Link;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Transfers;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Security;
using AiCtrl.Server.Transfers;
using AiCtrl.Tests.Server;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Transfers;

public sealed class TransferSoakTests : IAsyncLifetime
{
    private const long FileBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly CookieContainer _cookies = new();

    private readonly HttpClientHandler _handler;

    private readonly HttpClient _http;

    private TestServerHost _host = null!;

    private TransferSystem _transfers = null!;

    private ClientDb _db = null!;

    private WorkspaceRegistry _workspaces = null!;

    private string _workspaceRoot = string.Empty;

    private string _token = string.Empty;

    public TransferSoakTests()
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
        if (!string.Equals(Environment.GetEnvironmentVariable("AICTRL_SOAK"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set AICTRL_SOAK=1 to run the 2GB transfer soak test.");
        }

        var ct = TestContext.Current.CancellationToken;
        _host = await TestServerHost.StartAsync(
            _directory,
            transferOptions: new TransferOptions { Dir = Path.Combine(_directory, "transfers") });
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
        _token = _host.Services.GetRequiredService<AgentTokenService>().Create("ma_test", out _);
        var link = new TransferLinkState();
        link.Set(new ServerApiClient(_host.BaseUrl, _http), _token);
        _transfers = new TransferSystem(
            _db,
            _workspaces,
            link,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TransferSystem>.Instance,
            Path.Combine(_directory, "transfers"));
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        _handler.Dispose();
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Two_gigabyte_upload_resumes_and_matches_sha()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourcePath = Path.Combine(_workspaceRoot, "soak.bin");
        await using (var file = new FileStream(
            sourcePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            file.SetLength(FileBytes);
        }

        var sha = await HashFileAsync(sourcePath, ct);
        var workspaceId = _workspaces.Entries[0].WorkspaceId;
        using var create = await _http.PostAsJsonAsync(
            $"/api/v1/machines/ma_test/workspaces/{workspaceId}/transfers/downloads",
            new { relPath = "soak.bin", asZip = false },
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString()!;

        var api = new ServerApiClient(_host.BaseUrl, _http);
        var begin = await api.BeginTransferAsync(
            transferId,
            new TransferBeginRequest(FileBytes, sha),
            _token,
            ct);
        begin.ChunkSize.ShouldBe(8 * 1024 * 1024);
        begin.ChunksTotal.ShouldBe(256);

        var buffer = new byte[begin.ChunkSize];
        for (var index = 0; index < 128; index++)
        {
            await api.PutTransferChunkAsync(transferId, index, buffer, _token, ct);
        }

        var resumed = await api.BeginTransferAsync(
            transferId,
            new TransferBeginRequest(FileBytes, sha),
            _token,
            ct);
        resumed.ReceivedChunks.Count.ShouldBe(128);

        await _transfers.SendAsync(
            new FilesSendPayload(transferId, workspaceId, "soak.bin", AsZip: false),
            ct);

        using var status = await _http.GetAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}",
            ct);
        var body = await status.Content.ReadAsStringAsync(ct);
        status.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("data").GetProperty("status").GetString().ShouldBe("ready");
        parsed.RootElement.GetProperty("data").GetProperty("sha256").GetString().ShouldBe(sha);
        parsed.RootElement.GetProperty("data").GetProperty("receivedChunks").GetArrayLength().ShouldBe(256);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
