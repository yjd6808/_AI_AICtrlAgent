using System.Globalization;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Vpn;

public sealed record VpnRecoveryOptions
{
    public bool Enabled { get; init; }

    public string DiscordWebhookUrl { get; init; } = string.Empty;

    public bool DiscordEnabled { get; init; }

    public bool DiscordNotifyOnFailureOnly { get; init; }

    public bool AutoConnectVpn { get; init; } = true;

    public bool AutoResume { get; init; } = true;

    public bool HandleQuotaExceeded { get; init; } = true;

    public bool HandleRateLimited { get; init; } = true;

    public bool HandleAuthFailed { get; init; }

    public bool HandleModelUnavailable { get; init; }

    public bool HandleNetwork { get; init; } = true;

    public bool HandleStalled { get; init; } = true;

    public bool HandleUnknown { get; init; }

    public string ContinuationPrompt { get; init; } =
        VpnRecoverySettings.DefaultContinuationPrompt;

    public string? Country { get; init; }

    public string? City { get; init; }

    public IReadOnlyList<string> Servers { get; init; } = Array.Empty<string>();

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxAttemptsPerSession { get; init; } = 3;

    public int StallThresholdSeconds { get; init; }

    public Uri? WebhookUri =>
        VpnRecoverySettings.TryParseDiscordWebhook(DiscordWebhookUrl, out var uri) ? uri : null;

    public VpnRecoverySettings ToSettings() => new()
    {
        Enabled = Enabled,
        AutoConnectVpn = AutoConnectVpn,
        AutoResume = AutoResume,
        HandleQuotaExceeded = HandleQuotaExceeded,
        HandleRateLimited = HandleRateLimited,
        HandleAuthFailed = HandleAuthFailed,
        HandleModelUnavailable = HandleModelUnavailable,
        HandleNetwork = HandleNetwork,
        HandleStalled = HandleStalled,
        HandleUnknown = HandleUnknown,
        ContinuationPrompt = ContinuationPrompt,
        Country = Country,
        City = City,
        Servers = Servers,
        CooldownSeconds = (int)Math.Clamp(Cooldown.TotalSeconds, 0, 24 * 60 * 60),
        MaxAttemptsPerSession = MaxAttemptsPerSession,
        StallThresholdSeconds = StallThresholdSeconds,
        DiscordEnabled = DiscordEnabled,
        DiscordNotifyOnFailureOnly = DiscordNotifyOnFailureOnly,
    };

    public static VpnRecoveryOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("vpn").GetSection("recovery");
        var prompt = section["continuationPrompt"];
        var webhook = section["discordWebhookUrl"] ?? string.Empty;
        var handleStalled = ReadBool(section, "handleStalled", true);
        return new VpnRecoveryOptions
        {
            Enabled = ReadBool(section, "enabled", false),
            DiscordWebhookUrl = webhook,
            DiscordEnabled = ReadBool(
                section,
                "discordEnabled",
                !string.IsNullOrWhiteSpace(webhook)),
            DiscordNotifyOnFailureOnly = ReadBool(section, "discordNotifyOnFailureOnly", false),
            AutoConnectVpn = ReadBool(section, "autoConnectVpn", true),
            AutoResume = ReadBool(section, "autoResume", true),
            HandleQuotaExceeded = ReadBool(section, "handleQuotaExceeded", true),
            HandleRateLimited = ReadBool(section, "handleRateLimited", true),
            HandleAuthFailed = ReadBool(section, "handleAuthFailed", false),
            HandleModelUnavailable = ReadBool(section, "handleModelUnavailable", false),
            HandleNetwork = ReadBool(section, "handleNetwork", handleStalled),
            HandleStalled = handleStalled,
            HandleUnknown = ReadBool(section, "handleUnknown", false),
            ContinuationPrompt = string.IsNullOrWhiteSpace(prompt)
                ? VpnRecoverySettings.DefaultContinuationPrompt
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
            StallThresholdSeconds = ReadInt(section, "stallThresholdSeconds", 0, allowZero: true),
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

    private static int ReadInt(
        IConfiguration section,
        string key,
        int fallback,
        bool allowZero = false) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && (allowZero ? value >= 0 : value > 0)
            ? value
            : fallback;

    private static bool ReadBool(IConfiguration section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;
}
