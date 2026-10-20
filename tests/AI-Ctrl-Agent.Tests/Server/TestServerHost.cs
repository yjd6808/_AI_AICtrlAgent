using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using AiCtrl.Core.Runtime;
using AiCtrl.Server;
using AiCtrl.Server.Endpoints;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Tests.Server;

internal sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestServerHost(WebApplication app, string baseUrl, int port, string databasePath)
    {
        _app = app;
        BaseUrl = baseUrl;
        Port = port;
        DatabasePath = databasePath;
    }

    public string BaseUrl { get; }

    public int Port { get; }

    public string DatabasePath { get; }

    public IServiceProvider Services => _app.Services;

    public static string CreateDirectory() =>
        Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    public static async Task<TestServerHost> StartAsync(
        string directory,
        InMemoryServerSecretStore? secrets = null,
        IGeoIpLookup? geo = null,
        int? port = null,
        string? webDistPath = null,
        StreamOptions? streamOptions = null)
    {
        Directory.CreateDirectory(directory);
        var selectedPort = port ?? GetFreePort();
        var databasePath = Path.Combine(directory, "server.db");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{selectedPort}");
        builder.Logging.ClearProviders();
        builder.Services.AddAcRuntime();
        builder.Services.AddAcServer(
            new ServerDbOptions { Path = databasePath },
            new GeoOptions { LocalPort = selectedPort, EdgePort = selectedPort + 1 },
            new AuthOptions(),
            secrets ?? new InMemoryServerSecretStore(),
            streamOptions: streamOptions);
        builder.Services.AddSingleton(geo ?? new FixedGeoIpLookup("KR"));
        builder.Services.PostConfigure<CookieAuthenticationOptions>(
            GoogleAuthPolicy.CookieScheme,
            options =>
            {
                options.Cookie.Name = "aictrl-test";
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseRouting();
        app.UseMiddleware<GeoGateMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAcWebApp(webDistPath);
        app.MapAcServerEndpoints();
        app.MapPost("/test-login", async (HttpContext context) =>
        {
            var email = context.Request.Query["email"].FirstOrDefault() ?? "test@example.com";
            var name = context.Request.Query["name"].FirstOrDefault() ?? "Test User";
            var time = context.RequestServices.GetRequiredService<TimeProvider>();
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Email, email),
                    new Claim(ClaimTypes.NameIdentifier, email),
                    new Claim(ClaimTypes.Name, name),
                    new Claim("auth_time", time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ],
                GoogleAuthPolicy.CookieScheme);
            await context.SignInAsync(
                GoogleAuthPolicy.CookieScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
            return Results.NoContent();
        });
        app.MapHub<AgentHub>("/hubs/agent", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
        });
        app.MapHub<UiHub>("/hubs/ui");
        await app.StartAsync().ConfigureAwait(false);
        return new TestServerHost(app, $"http://127.0.0.1:{selectedPort}", selectedPort, databasePath);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class FixedGeoIpLookup : IGeoIpLookup
{
    private readonly IReadOnlyDictionary<string, string> _countries;

    public FixedGeoIpLookup(string country)
        : this(new Dictionary<string, string>(StringComparer.Ordinal) { ["*"] = country })
    {
    }

    public FixedGeoIpLookup(IReadOnlyDictionary<string, string> countries)
    {
        _countries = countries;
    }

    public string? GetCountry(IPAddress address) =>
        _countries.TryGetValue(address.ToString(), out var country)
            ? country
            : _countries.GetValueOrDefault("*");
}
