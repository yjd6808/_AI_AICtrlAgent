namespace AiCtrl.Core.Config;

public sealed class AcConfigValidator
{
    public const string ConfigVersionKey = "configVersion";

    private readonly string[] _knownKeyPatterns;

    public AcConfigValidator(IEnumerable<string> knownKeyPatterns)
    {
        ArgumentNullException.ThrowIfNull(knownKeyPatterns);
        _knownKeyPatterns = knownKeyPatterns.ToArray();
    }

    public AcConfigValidationResult Validate(
        IReadOnlyDictionary<string, string?> data,
        IReadOnlyDictionary<string, int> keyLines,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(keyLines);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var issues = new List<AcConfigIssue>();
        ValidateConfigVersion(data, keyLines, issues);

        foreach (var pair in data)
        {
            if (pair.Key.Equals(ConfigVersionKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsKnownKey(pair.Key))
            {
                issues.Add(new AcConfigIssue(
                    AcConfigIssueSeverity.Warning,
                    pair.Key,
                    GetLine(keyLines, pair.Key),
                    $"unknown key '{pair.Key}'"));
            }
        }

        return issues.Count == 0 ? AcConfigValidationResult.Empty : new AcConfigValidationResult(issues);
    }

    private static void ValidateConfigVersion(
        IReadOnlyDictionary<string, string?> data,
        IReadOnlyDictionary<string, int> keyLines,
        List<AcConfigIssue> issues)
    {
        var line = GetLine(keyLines, ConfigVersionKey);

        if (!data.TryGetValue(ConfigVersionKey, out var raw))
        {
            issues.Add(new AcConfigIssue(
                AcConfigIssueSeverity.Error,
                ConfigVersionKey,
                line,
                $"필수 키 '{ConfigVersionKey}' 누락"));
            return;
        }

        if (!int.TryParse(raw, out var version))
        {
            issues.Add(new AcConfigIssue(
                AcConfigIssueSeverity.Error,
                ConfigVersionKey,
                line,
                $"{ConfigVersionKey} 값은 정수여야 합니다: '{raw}'"));
            return;
        }

        if (version > 1)
        {
            issues.Add(new AcConfigIssue(
                AcConfigIssueSeverity.Error,
                ConfigVersionKey,
                line,
                $"지원하지 않는 {ConfigVersionKey} {version} (현재 1)"));
        }
        else if (version < 1)
        {
            issues.Add(new AcConfigIssue(
                AcConfigIssueSeverity.Warning,
                ConfigVersionKey,
                line,
                $"{ConfigVersionKey} {version} → 1 메모리 마이그레이션 적용"));
        }
    }

    private bool IsKnownKey(string key)
    {
        foreach (var pattern in _knownKeyPatterns)
        {
            if (MatchesPattern(pattern, key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string pattern, string key)
    {
        var patternSegments = pattern.Split(':');
        var keySegments = key.Split(':');
        if (patternSegments.Length != keySegments.Length)
        {
            return false;
        }

        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] == "*")
            {
                continue;
            }

            if (!patternSegments[i].Equals(keySegments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetLine(IReadOnlyDictionary<string, int> keyLines, string key) =>
        keyLines.TryGetValue(key, out var line) ? line : 0;
}
