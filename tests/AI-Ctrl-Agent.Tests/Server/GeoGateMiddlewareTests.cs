using System.Net;
using AiCtrl.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class GeoGateMiddlewareTests
{
    [Fact]
    public async Task Local_listener_bypasses_geo_gate()
    {
        var context = Context("/api/v1/machines", localPort: 5080);
        var called = false;
        var middleware = Middleware(_ => called = true, new FixedGeoIpLookup("US"), new GeoOptions
        {
            LocalPort = 5080,
            EdgePort = 5081,
        });

        await InvokeAsync(middleware, context);

        called.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Allowed_country_passes_on_edge()
    {
        var context = Context("/api/v1/machines", localPort: 5081, edgeIp: "1.1.1.1");
        var called = false;
        var middleware = Middleware(_ => called = true, new FixedGeoIpLookup("KR"), Options());

        await InvokeAsync(middleware, context);

        called.ShouldBeTrue();
        context.Items[GeoGateMiddleware.CountryItemKey].ShouldBe("KR");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Blocked_country_returns_forbidden()
    {
        var context = Context("/api/v1/machines", localPort: 5081, edgeIp: "8.8.8.8");
        var middleware = Middleware(_ => throw new InvalidOperationException("next must not run"), new FixedGeoIpLookup("US"), Options());

        await InvokeAsync(middleware, context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.ContentType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Lookup_failure_fails_closed_when_configured()
    {
        var context = Context("/api/v1/machines", localPort: 5081, edgeIp: "8.8.8.8");
        var middleware = Middleware(_ => throw new InvalidOperationException("next must not run"), new NullGeoIpLookup(), Options());

        await InvokeAsync(middleware, context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Allowed_cidr_bypasses_country_check()
    {
        var context = Context("/api/v1/machines", localPort: 5081, edgeIp: "10.1.2.3");
        var called = false;
        var options = Options() with { AllowCidrs = ["10.0.0.0/8"] };
        var middleware = Middleware(_ => called = true, new FixedGeoIpLookup("US"), options);

        await InvokeAsync(middleware, context);

        called.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Cloudflare_header_is_used_and_forwarded_for_is_ignored()
    {
        var context = Context("/api/v1/machines", localPort: 5081, edgeIp: "8.8.8.8");
        context.Request.Headers["X-Forwarded-For"] = "1.1.1.1";
        var lookup = new FixedGeoIpLookup(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.1.1.1"] = "KR",
            ["8.8.8.8"] = "US",
        });
        var middleware = Middleware(_ => throw new InvalidOperationException("next must not run"), lookup, Options());

        await InvokeAsync(middleware, context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Items[GeoGateMiddleware.IpItemKey].ShouldBe("8.8.8.8");
    }

    [Fact]
    public async Task Agent_hub_is_exempt_on_edge()
    {
        var context = Context("/hubs/agent", localPort: 5081);
        var called = false;
        var middleware = Middleware(_ => called = true, new NullGeoIpLookup(), Options());

        await InvokeAsync(middleware, context);

        called.ShouldBeTrue();
    }

    private static GeoOptions Options() => new()
    {
        LocalPort = 5080,
        EdgePort = 5081,
        AllowCountries = ["KR"],
        FailClosed = true,
    };

    private static GeoGateMiddleware Middleware(
        Action<HttpContext> next,
        IGeoIpLookup lookup,
        GeoOptions options) =>
        new(context =>
        {
            next(context);
            return Task.CompletedTask;
        }, options, lookup, NullLogger<GeoGateMiddleware>.Instance);

    private static DefaultHttpContext Context(string path, int localPort, string? edgeIp = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        if (edgeIp is not null)
        {
            context.Request.Headers["CF-Connecting-IP"] = edgeIp;
        }

        return context;
    }

    private static async Task InvokeAsync(GeoGateMiddleware middleware, HttpContext context) =>
        await middleware.InvokeAsync(context);
}

internal sealed class NullGeoIpLookup : IGeoIpLookup
{
    public string? GetCountry(IPAddress address) => null;
}
