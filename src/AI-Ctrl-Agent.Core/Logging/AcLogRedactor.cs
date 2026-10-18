using System.Text.RegularExpressions;

namespace AiCtrl.Core.Logging;

public static partial class AcLogRedactor
{
    private static readonly Replacement[] Rules =
    [
        new(AuthorizationRegex(), "${1}****"),
        new(BearerRegex(), "${1}****"),
        new(PatRegex(), "${1}****"),
        new(PasswordRegex(), "${1}****"),
        new(JwtRegex(), "****"),
        new(EnrollCodeRegex(), "${1}****"),
        new(CliCodeRegex(), "${1}****"),
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var rule in Rules)
        {
            result = rule.Pattern.Replace(result, rule.ReplacementText);
        }

        return result;
    }

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*)(?:bearer\s+)?\S+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(act_)[A-Za-z0-9_-]+")]
    private static partial Regex PatRegex();

    [GeneratedRegex(@"(?i)(password\s*[:=]\s*)[^\s,;""]+")]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*(?:\.[A-Za-z0-9_-]*)+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)(enroll\s*[_-]?\s*code\s*[:=]\s*)\S+")]
    private static partial Regex EnrollCodeRegex();

    [GeneratedRegex(@"(?i)(--code\s+)\S+")]
    private static partial Regex CliCodeRegex();

    private sealed record Replacement(Regex Pattern, string ReplacementText);
}
