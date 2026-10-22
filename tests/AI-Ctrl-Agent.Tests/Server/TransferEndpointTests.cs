using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        _host = await TestServerHost.StartAsync(_directory);
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
    public async Task Agent_transfer_endpoints_require_agent_token()
    {
        using var response = await _browser.GetAsync(
            "/api/v1/agent/transfers/tr_00000000000000000000000000000000",
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
