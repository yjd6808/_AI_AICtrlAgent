using System.Net;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class WebAppTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly HttpClient _client = new();

    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dist = Path.Combine(_directory, "dist");
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), "<html><body>AI-Ctrl</body></html>", ct);
        await File.WriteAllTextAsync(Path.Combine(dist, "assets", "app.js"), "console.log('ok');", ct);
        _host = await TestServerHost.StartAsync(
            Path.Combine(_directory, "server"),
            webDistPath: dist);
        _client.BaseAddress = new Uri(_host.BaseUrl);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Root_and_spa_routes_serve_the_index()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rootResponse = await _client.GetAsync("/", ct);
        var root = await rootResponse.Content.ReadAsStringAsync(ct);
        var route = await _client.GetStringAsync("/m/machine-1/agents/opencode", ct);

        rootResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        rootResponse.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        // Exact match: the missing-dist probe string also contains "AI-Ctrl".
        root.ShouldBe("<html><body>AI-Ctrl</body></html>");
        route.ShouldBe("<html><body>AI-Ctrl</body></html>");
    }

    [Fact]
    public async Task Static_assets_are_served_from_the_dist_directory()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = await _client.GetStringAsync("/assets/app.js", ct);

        script.ShouldContain("console.log('ok');");
    }

    [Fact]
    public async Task Unmatched_api_paths_do_not_fall_back_to_index()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.GetAsync("/api/v1/not-a-route", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain("AI-Ctrl");
    }
}
