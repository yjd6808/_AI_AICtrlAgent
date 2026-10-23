using System.Globalization;
using System.Text.RegularExpressions;
using AiCtrl.Core.Domain;

namespace AiCtrl.Client.Vpn;

public sealed record VpnFailureAssessment(
    string ServerStatus,
    TimeSpan? RetryAfter,
    string? Message);

public static partial class VpnFailureParser
{
    public static VpnFailureAssessment Assess(
        ErrorClass errorClass,
        string? message,
        RetryInfo? retry,
        VpnRecoveryNoticeKind kind,
        DateTimeOffset now)
    {
        var status = errorClass switch
        {
            ErrorClass.QuotaExceeded => VpnServerStatuses.QuotaExhausted,
            ErrorClass.RateLimited => VpnServerStatuses.QuotaExhausted,
            ErrorClass.Network => VpnServerStatuses.OpenCodeTimeout,
            _ => kind switch
            {
                VpnRecoveryNoticeKind.QuotaExceeded => VpnServerStatuses.QuotaExhausted,
                VpnRecoveryNoticeKind.RateLimited => VpnServerStatuses.QuotaExhausted,
                _ => VpnServerStatuses.OpenCodeTimeout,
            },
        };
        return new VpnFailureAssessment(status, RetryAfter(retry, message, now), Trim(message ?? retry?.Message));
    }

    public static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var korean = KoreanRetry().Match(value);
        if (korean.Success && TrySeconds(korean.Groups[1].Value, out var koreanSeconds))
        {
            return TimeSpan.FromSeconds(koreanSeconds);
        }

        var english = EnglishRetry().Match(value);
        if (english.Success && TrySeconds(english.Groups[1].Value, out var englishSeconds))
        {
            return TimeSpan.FromSeconds(englishSeconds);
        }

        var koreanShort = KoreanShort().Match(value);
        return koreanShort.Success && TrySeconds(koreanShort.Groups[1].Value, out var shortSeconds)
            ? TimeSpan.FromSeconds(shortSeconds)
            : null;
    }

    private static TimeSpan? RetryAfter(RetryInfo? retry, string? message, DateTimeOffset now)
    {
        if (retry?.NextAt is { } nextAt)
        {
            return nextAt > now ? nextAt - now : TimeSpan.Zero;
        }

        return ParseRetryAfter(retry?.Message) ?? ParseRetryAfter(message);
    }

    private static bool TrySeconds(string value, out double seconds) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
        && seconds >= 0;

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > 400 ? normalized[..400] : normalized;
    }

    [GeneratedRegex("재시도\\s*중\\s*(\\d+(?:\\.\\d+)?)\\s*초", RegexOptions.CultureInvariant)]
    private static partial Regex KoreanRetry();

    [GeneratedRegex("(?:retry(?:ing)?(?:\\s+in)?|after)\\s*(\\d+(?:\\.\\d+)?)\\s*(?:seconds?|secs?|s\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishRetry();

    [GeneratedRegex("(\\d+(?:\\.\\d+)?)\\s*초", RegexOptions.CultureInvariant)]
    private static partial Regex KoreanShort();
}
