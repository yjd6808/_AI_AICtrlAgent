using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Server.Security;

public sealed record GeoOptions
{
    public string DbPath { get; init; } = @"C:\AiCtrl\AgentServer\data\GeoLite2-Country.mmdb";

    public IReadOnlyList<string> AllowCountries { get; init; } = ["KR"];

    public IReadOnlyList<string> AllowCidrs { get; init; } = Array.Empty<string>();

    public bool FailClosed { get; init; } = true;

    public int LocalPort { get; init; } = 5080;

    public int EdgePort { get; init; } = 5081;

    public long MaximumReceiveMessageSize { get; init; } = 2 * 1024 * 1024;

    public static GeoOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("geo");
        var countries = section.GetSection("allowCountries").GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
        var cidrs = section.GetSection("allowCidrs").GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();

        return new GeoOptions
        {
            DbPath = string.IsNullOrWhiteSpace(section["dbPath"]) ? new GeoOptions().DbPath : section["dbPath"]!,
            AllowCountries = countries.Length == 0 ? ["KR"] : countries,
            AllowCidrs = cidrs,
            FailClosed = !string.Equals(section["failClosed"], "false", StringComparison.OrdinalIgnoreCase),
            LocalPort = ParsePort(configuration["kestrel:endpoints:local:url"], 5080),
            EdgePort = ParsePort(configuration["kestrel:endpoints:edge:url"], 5081),
            MaximumReceiveMessageSize = ParseLong(configuration["agentHub:maximumReceiveMessageSize"], 2 * 1024 * 1024),
        };
    }

    private static int ParsePort(string? url, int fallback)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Port;
        }

        return int.TryParse(url, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : fallback;
    }

    private static long ParseLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;
}

public static class GeoGatePaths
{
    public static bool IsExempt(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/api/v1/enroll", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/api/v1/agent/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/hubs/agent", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/signin-google", StringComparison.OrdinalIgnoreCase);
    }
}

public static class CidrRange
{
    public static bool Contains(string cidr, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(cidr);
        var parts = cidr.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var network))
        {
            return false;
        }

        var prefix = network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)
            || prefix < 0 || prefix > (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)))
        {
            return false;
        }

        var normalizedNetwork = Normalize(network);
        var normalizedAddress = Normalize(address);
        if (normalizedNetwork.AddressFamily != normalizedAddress.AddressFamily)
        {
            return false;
        }

        var networkBytes = normalizedNetwork.GetAddressBytes();
        var addressBytes = normalizedAddress.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
