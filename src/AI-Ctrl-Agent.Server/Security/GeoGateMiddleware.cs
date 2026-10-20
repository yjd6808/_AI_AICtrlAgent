using System.Net;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Serialization;

namespace AiCtrl.Server.Security;

public sealed class GeoGateMiddleware
{
    public const string CountryItemKey = "geo.country";

    public const string IpItemKey = "geo.ip";

    private const string EdgeHeader = "CF-Connecting-IP";

    private readonly RequestDelegate _next;

    private readonly GeoOptions _options;

    private readonly IGeoIpLookup _lookup;

    private readonly ILogger<GeoGateMiddleware> _logger;

    public GeoGateMiddleware(
        RequestDelegate next,
        GeoOptions options,
        IGeoIpLookup lookup,
        ILogger<GeoGateMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _options = options;
        _lookup = lookup;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var localPort = context.Connection.LocalPort;
        if (localPort == _options.LocalPort)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (GeoGatePaths.IsExempt(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (localPort != _options.EdgePort)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidHost, "Unknown listener endpoint.").ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers[EdgeHeader].ToString();
        if (!IPAddress.TryParse(header, out var address))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidHost, "CF-Connecting-IP is required on the edge endpoint.").ConfigureAwait(false);
            return;
        }

        address = CidrRange.Normalize(address);
        context.Items[IpItemKey] = address.ToString();
        var country = _lookup.GetCountry(address);
        if (country is not null)
        {
            context.Items[CountryItemKey] = country;
        }

        if (_options.AllowCidrs.Any(cidr => CidrRange.Contains(cidr, address)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (country is null)
        {
            if (_options.FailClosed)
            {
                _logger.LogWarning("Geo lookup failed for {Ip}; request blocked.", address);
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, AcErrorCodes.GeoBlocked, "Geo lookup failed.").ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_options.AllowCountries.Contains(country, StringComparer.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("Geo blocked {Ip} ({Country}) for {Path}.", address, country, context.Request.Path);
        await WriteProblemAsync(context, StatusCodes.Status403Forbidden, AcErrorCodes.GeoBlocked, "Country is not allowed.").ConfigureAwait(false);
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new AcProblemDetails(
            "about:blank",
            status == StatusCodes.Status400BadRequest ? "Bad Request" : "Forbidden",
            status,
            detail,
            context.Request.Path,
            code);
        await JsonSerializer.SerializeAsync(context.Response.Body, problem, AcJsonContext.Default.AcProblemDetails, context.RequestAborted)
            .ConfigureAwait(false);
    }
}
