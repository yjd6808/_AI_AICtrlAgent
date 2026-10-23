using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Vpn;

public sealed record VpnRecoveryOptions
{
    public bool Enabled { get; init; }

    public string DiscordWebhookUrl { get; init; } = string.Empty;

    public bool AutoConnectVpn { get; init; } = true;

    public bool AutoResume { get; init; } = true;

    public bool HandleRateLimited { get; init; } = true;

    public bool HandleStalled { get; init; } = true;

    public string ContinuationPrompt { get; init; } =
        "Continue from where you stopped. Do not repeat completed work.";

    public string? Country { get; init; }

    public string? City { get; init; }

    public IReadOnlyList<string> Servers { get; init; } = Array.Empty<string>();

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxAttemptsPerSession { get; init; } = 3;

    public Uri? WebhookUri =>
        Uri.TryCreate(DiscordWebhookUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri
            : null;

    public static VpnRecoveryOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("vpn").GetSection("recovery");
        var prompt = section["continuationPrompt"];
        return new VpnRecoveryOptions
        {
            Enabled = ReadBool(section, "enabled", false),
            DiscordWebhookUrl = section["discordWebhookUrl"] ?? string.Empty,
            AutoConnectVpn = ReadBool(section, "autoConnectVpn", true),
            AutoResume = ReadBool(section, "autoResume", true),
            HandleRateLimited = ReadBool(section, "handleRateLimited", true),
            HandleStalled = ReadBool(section, "handleStalled", true),
            ContinuationPrompt = string.IsNullOrWhiteSpace(prompt)
                ? new VpnRecoveryOptions().ContinuationPrompt
                : prompt,
            Country = ReadOptionalCode(section["country"]),
            City = ReadOptionalText(section["city"]),
            Servers = section.GetSection("servers")
                .GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray(),
            Cooldown = TimeSpan.FromSeconds(ReadDouble(section, "cooldownSeconds", 600)),
            MaxAttemptsPerSession = ReadInt(section, "maxAttemptsPerSession", 3),
        };
    }

    private static string? ReadOptionalCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? ReadOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double ReadDouble(IConfiguration section, string key, double fallback) =>
        double.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : fallback;

    private static int ReadInt(IConfiguration section, string key, int fallback) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : fallback;

    private static bool ReadBool(IConfiguration section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;
}
