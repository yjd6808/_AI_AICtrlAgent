namespace AiCtrl.Core.Config;

public enum AcConfigIssueSeverity
{
    Warning,
    Error,
}

public sealed record AcConfigIssue(AcConfigIssueSeverity Severity, string Key, int Line, string Message)
{
    public string Format() => Line > 0 ? $"{Message} (line {Line})" : Message;
}

public sealed class AcConfigValidationResult
{
    public static AcConfigValidationResult Empty { get; } = new([]);

    public AcConfigValidationResult(IReadOnlyList<AcConfigIssue> issues)
    {
        Issues = issues;
    }

    public IReadOnlyList<AcConfigIssue> Issues { get; }

    public bool HasErrors => Issues.Any(issue => issue.Severity == AcConfigIssueSeverity.Error);
}
