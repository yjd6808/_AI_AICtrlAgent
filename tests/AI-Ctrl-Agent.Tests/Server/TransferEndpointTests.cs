using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Transfers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class TransferEndpointTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly CookieContainer _cookies = new();

    private readonly HttpClientHandler _handler;

    private readonly HttpClient _browser;

    private TestServerHost _host = null!;

    public TransferEndpointTests()
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        _browser = new HttpClient(_handler);
    }

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(
            _directory,
            transferOptions: new TransferOptions { Dir = Path.Combine(_directory, "transfers") });
        _browser.BaseAddress = new Uri(_host.BaseUrl);
        using var login = await _browser.PostAsync(
            "/test-login?email=test%40example.com&name=Test+User",
            null,
            TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        _handler.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Upload_transfer_accepts_chunks_and_serves_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("hello transfer");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using var create = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/transfers/uploads",
            new
            {
                relDir = "docs",
                fileName = "note.txt",
                size = content.Length,
                sha256 = sha,
                conflict = "fail",
            },
            ct);
        var createBody = await create.Content.ReadAsStringAsync(ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created, createBody);
        using var created = JsonDocument.Parse(createBody);
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();
        transferId.ShouldNotBeNullOrWhiteSpace();

        using var chunk = new ByteArrayContent(content);
        chunk.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var put = await _browser.PutAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/chunks/0",
            chunk,
            ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var complete = await _browser.PostAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/complete",
            null,
            ct);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var completed = JsonDocument.Parse(await complete.Content.ReadAsStringAsync(ct));
        completed.RootElement.GetProperty("data").GetProperty("status").GetString().ShouldBe("ready");
        completed.RootElement.GetProperty("data").GetProperty("receivedChunks").GetArrayLength().ShouldBe(1);

        var served = await _browser.GetByteArrayAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/content",
            ct);
        served.ShouldBe(content);
    }

    [Fact]
    public async Task Architecture_transfer_aliases_are_supported()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("alias transfer");
        using var create = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/uploads",
            new
            {
                relDir = "",
                fileName = "alias.txt",
                size = content.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                conflict = "fail",
            },
            ct);
        var createBody = await create.Content.ReadAsStringAsync(ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created, createBody);
        using var created = JsonDocument.Parse(createBody);
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();

        using var chunk = new ByteArrayContent(content);
        using var put = await _browser.PutAsync(
            $"/api/v1/transfers/{transferId}/chunks/0",
            chunk,
            ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var complete = await _browser.PostAsync(
            $"/api/v1/transfers/{transferId}/complete",
            null,
            ct);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var status = await _browser.GetAsync($"/api/v1/transfers/{transferId}", ct);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
        statusBody.RootElement.GetProperty("data").GetProperty("status").GetString().ShouldBe("ready");

        var served = await _browser.GetByteArrayAsync($"/api/v1/transfers/{transferId}/content", ct);
        served.ShouldBe(content);
    }

    [Fact]
    public async Task Empty_transfer_completes_without_chunks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var create = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/transfers/uploads",
            new
            {
                relDir = "",
                fileName = "empty.txt",
                size = 0,
                sha256 = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
                conflict = "fail",
            },
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();

        using var complete = await _browser.PostAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/complete",
            null,
            ct);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var completed = JsonDocument.Parse(await complete.Content.ReadAsStringAsync(ct));
        completed.RootElement.GetProperty("data").GetProperty("status").GetString().ShouldBe("ready");

        var served = await _browser.GetByteArrayAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/content",
            ct);
        served.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transfer_rejects_traversal_and_wrong_chunk_length()
    {
        var ct = TestContext.Current.CancellationToken;
        using var traversal = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/transfers/uploads",
            new
            {
                relDir = "../escape",
                fileName = "note.txt",
                size = 4,
                sha256 = (string?)null,
                conflict = "fail",
            },
            ct);
        traversal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await traversal.Content.ReadAsStringAsync(ct));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("path_invalid");

        using var create = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/transfers/uploads",
            new
            {
                relDir = "",
                fileName = "note.txt",
                size = 4,
                sha256 = (string?)null,
                conflict = "fail",
            },
            ct);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();

        using var shortChunk = new ByteArrayContent(Encoding.UTF8.GetBytes("ab"));
        using var put = await _browser.PutAsync(
            $"/api/v1/machines/ma_test/transfers/{transferId}/chunks/0",
            shortChunk,
            ct);
        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Download_zip_uses_zip_file_name()
    {
        var ct = TestContext.Current.CancellationToken;
        using var create = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/transfers/downloads",
            new
            {
                relPath = "docs",
                asZip = true,
            },
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();

        var factory = _host.Services.GetRequiredService<IDbContextFactory<AcDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Transfers.SingleAsync(x => x.Id == transferId, ct);
        entity.FileName.ShouldBe("docs.zip");
    }

    [Fact]
    public async Task Expired_transfer_is_marked_and_content_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var create = await _browser.PostAsJsonAsync(
            "/api/v1/machines/ma_test/workspaces/ws_test/transfers/uploads",
            new
            {
                relDir = "",
                fileName = "expired.txt",
                size = 4,
                sha256 = (string?)null,
                conflict = "fail",
            },
            ct);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        var transferId = created.RootElement.GetProperty("data").GetProperty("transferId").GetString();

        var factory = _host.Services.GetRequiredService<IDbContextFactory<AcDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var entity = await db.Transfers.SingleAsync(x => x.Id == transferId, ct);
            entity.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(ct);
        }

        var store = _host.Services.GetRequiredService<TransferStore>();
        await File.WriteAllBytesAsync(store.DataPath(transferId!), [1, 2, 3, 4], ct);
        var count = await store.CleanupExpiredAsync(ct);
        count.ShouldBe(1);

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var entity = await db.Transfers.SingleAsync(x => x.Id == transferId, ct);
            entity.Status.ShouldBe(TransferStatuses.Expired);
        }

        File.Exists(store.DataPath(transferId!)).ShouldBeFalse();
    }

    [Fact]
    public async Task Agent_transfer_endpoints_require_agent_token()
    {
        using var response = await _browser.GetAsync(
            "/api/v1/agent/transfers/tr_00000000000000000000000000000000",
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
