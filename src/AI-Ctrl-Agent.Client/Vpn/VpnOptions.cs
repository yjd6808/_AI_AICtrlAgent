using System.Globalization;
using AiCtrl.Client.Storage;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Vpn;

public sealed record VpnOptions
{
    public bool Enabled { get; init; } = true;

    public bool HelperEnabled { get; init; } = true;

    public string HelperTokenPath { get; init; } = ClientPaths.VpnHelperTokenPath;

    public string CliPath { get; init; } = string.Empty;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public string DefaultCountry { get; init; } = "JP";

    public string HistoryPath { get; init; } =
        Path.Combine(ClientPaths.DataDirectory, "vpn-history.json");

    public VpnFallbackOptions Fallback { get; init; } = VpnFallbackOptions.Default;

    public static VpnOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("vpn");
        var defaultCountry = section["defaultCountry"];
        return new VpnOptions
        {
            Enabled = ReadBool(section, "enabled", true),
            HelperEnabled = ReadBool(section, "helperEnabled", true),
            HelperTokenPath = string.IsNullOrWhiteSpace(section["helperTokenPath"])
                ? ClientPaths.VpnHelperTokenPath
                : section["helperTokenPath"]!,
            CliPath = section["cliPath"] ?? string.Empty,
            ConnectTimeout = TimeSpan.FromSeconds(ReadDouble(section, "connectTimeoutSeconds", 90)),
            QueryTimeout = TimeSpan.FromSeconds(ReadDouble(section, "queryTimeoutSeconds", 20)),
            StatusPollInterval = TimeSpan.FromSeconds(ReadDouble(section, "statusPollSeconds", 30)),
            DefaultCountry = string.IsNullOrWhiteSpace(defaultCountry) ? "JP" : defaultCountry.ToUpperInvariant(),
            HistoryPath = string.IsNullOrWhiteSpace(section["historyPath"])
                ? Path.Combine(ClientPaths.DataDirectory, "vpn-history.json")
                : section["historyPath"]!,
            Fallback = ReadFallback(section.GetSection("fallback")),
        };
    }

    private static VpnFallbackOptions ReadFallback(IConfiguration section)
    {
        var mode = Enum.TryParse<VpnFallbackMode>(section["mode"], ignoreCase: true, out var parsed)
            ? parsed
            : VpnFallbackMode.TunnelReady;
        var readyUri = Uri.TryCreate(section["readyUrl"], UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri
                : VpnFallbackOptions.Default.ReadyUri;
        return new VpnFallbackOptions
        {
            Mode = mode,
            ReadyUri = readyUri,
            Timeout = TimeSpan.FromMinutes(ReadDouble(section, "minutes", 5)),
        };
    }

    private static double ReadDouble(IConfiguration section, string key, double fallback) =>
        double.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : fallback;

    private static bool ReadBool(IConfiguration section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;
}
