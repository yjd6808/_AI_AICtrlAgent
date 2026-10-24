namespace AiCtrl.Core.Domain;

using System.Text.Json.Serialization;

public sealed record VpnRecoverySettings
{
    public const string DefaultContinuationPrompt =
        "Continue from where you stopped. Do not repeat completed work.";

    public bool Enabled { get; init; }

    public bool AutoConnectVpn { get; init; } = true;

    public bool AutoResume { get; init; } = true;

    public bool HandleQuotaExceeded { get; init; } = true;

    public bool HandleRateLimited { get; init; } = true;

    public bool HandleAuthFailed { get; init; }

    public bool HandleModelUnavailable { get; init; }

    public bool HandleNetwork { get; init; } = true;

    public bool HandleStalled { get; init; } = true;

    public bool HandleUnknown { get; init; }

    public string ContinuationPrompt { get; init; } = DefaultContinuationPrompt;

    public string? Country { get; init; }

    public string? City { get; init; }

    public IReadOnlyList<string> Servers { get; init; } = Array.Empty<string>();

    public int CooldownSeconds { get; init; } = 600;

    public int MaxAttemptsPerSession { get; init; } = 3;

    public int StallThresholdSeconds { get; init; }

    public bool DiscordEnabled { get; init; }

    public bool DiscordNotifyOnFailureOnly { get; init; }

    [JsonIgnore]
    public TimeSpan Cooldown => TimeSpan.FromSeconds(Math.Max(0, CooldownSeconds));

    [JsonIgnore]
    public TimeSpan StallThreshold => TimeSpan.FromSeconds(Math.Max(0, StallThresholdSeconds));

    public VpnRecoverySettings Normalize()
    {
        var prompt = string.IsNullOrWhiteSpace(ContinuationPrompt)
            ? DefaultContinuationPrompt
            : ContinuationPrompt.Trim();
        if (prompt.Length > 4000)
        {
            prompt = prompt[..4000];
        }

        var servers = (Servers ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length <= 200)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        return this with
        {
            ContinuationPrompt = prompt,
            Country = NormalizeCode(Country),
            City = NormalizeText(City, 80),
            Servers = servers,
            CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 24 * 60 * 60),
            MaxAttemptsPerSession = Math.Clamp(MaxAttemptsPerSession, 1, 10),
            StallThresholdSeconds = Math.Clamp(StallThresholdSeconds, 0, 24 * 60 * 60),
        };
    }

    public bool ShouldHandle(ErrorClass errorClass, bool stalled = false) => errorClass switch
    {
        ErrorClass.QuotaExceeded => HandleQuotaExceeded,
        ErrorClass.RateLimited => HandleRateLimited,
        ErrorClass.AuthFailed => HandleAuthFailed,
        ErrorClass.ModelUnavailable => HandleModelUnavailable,
        ErrorClass.Network => HandleNetwork,
        ErrorClass.Unknown => stalled ? HandleStalled : HandleUnknown,
        _ => false,
    };

    public VpnRecoveryView ToView(DateTimeOffset updatedAt, bool discordWebhookConfigured)
    {
        var normalized = Normalize();
        return new VpnRecoveryView(
            normalized.Enabled,
            normalized.AutoConnectVpn,
            normalized.AutoResume,
            normalized.HandleQuotaExceeded,
            normalized.HandleRateLimited,
            normalized.HandleAuthFailed,
            normalized.HandleModelUnavailable,
            normalized.HandleNetwork,
            normalized.HandleStalled,
            normalized.HandleUnknown,
            normalized.ContinuationPrompt,
            normalized.Country,
            normalized.City,
            normalized.Servers,
            normalized.CooldownSeconds,
            normalized.MaxAttemptsPerSession,
            normalized.StallThresholdSeconds,
            normalized.DiscordEnabled,
            normalized.DiscordNotifyOnFailureOnly,
            discordWebhookConfigured,
            updatedAt);
    }

    public static bool TryParseDiscordWebhook(string? value, out Uri? webhook)
    {
        webhook = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsDiscordHost(uri.Host)
            || !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        webhook = uri;
        return true;
    }

    private static bool IsDiscordHost(string host) =>
        host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("canary.discord.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("ptb.discord.com", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }
}
