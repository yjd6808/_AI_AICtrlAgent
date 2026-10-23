using System.Text.RegularExpressions;
using AiCtrl.Core.Domain;

namespace AiCtrl.Client.Diagnostics;

public sealed record ErrorRule(
    ErrorClass Class,
    IReadOnlyList<int>? StatusCodes,
    string? NameRegex,
    string? MessageRegex,
    bool RequireAll = false)
{
    public bool Matches(string? name, string? message, int? statusCode)
    {
        var conditions = new List<bool>();
        if (StatusCodes is { Count: > 0 })
        {
            conditions.Add(statusCode is { } code && StatusCodes.Contains(code));
        }

        if (NameRegex is not null)
        {
            conditions.Add(name is not null && Regex.IsMatch(name, NameRegex));
        }

        if (MessageRegex is not null)
        {
            conditions.Add(message is not null && Regex.IsMatch(message, MessageRegex));
        }

        if (conditions.Count == 0)
        {
            return false;
        }

        return RequireAll ? conditions.TrueForAll(condition => condition) : conditions.Exists(condition => condition);
    }
}

public sealed class ErrorClassifier
{
    public static readonly IReadOnlyList<ErrorRule> DefaultRules = new ErrorRule[]
    {
        new(ErrorClass.Aborted, null, "^MessageAbortedError$", null),
        new(ErrorClass.QuotaExceeded, new[] { 402 }, null, "(?i)quota|insufficient|credit|usage limit|free tier|free usage|subscribe to (?:go|pro)"),
        new(ErrorClass.RateLimited, new[] { 429 }, null, "(?i)rate.?limit|too many requests"),
        new(ErrorClass.AuthFailed, new[] { 401, 403 }, null, null),
        new(ErrorClass.ModelUnavailable, new[] { 404, 503 }, null, "(?i)model", RequireAll: true),
        new(ErrorClass.Network, null, null, "(?i)ECONN|ETIMEDOUT|fetch failed|socket|timed? ?out|timeout"),
    };

    private readonly IReadOnlyList<ErrorRule> _rules;

    public ErrorClassifier(IReadOnlyList<ErrorRule>? rules = null)
    {
        _rules = rules ?? DefaultRules;
    }

    public ErrorInfo Classify(string? name, string? message, int? statusCode, string? rawJson = null)
    {
        foreach (var rule in _rules)
        {
            if (rule.Matches(name, message, statusCode))
            {
                return new ErrorInfo(rule.Class, name ?? string.Empty, message ?? string.Empty, statusCode, rawJson);
            }
        }

        return new ErrorInfo(ErrorClass.Unknown, name ?? string.Empty, message ?? string.Empty, statusCode, rawJson);
    }

    public ErrorInfo ClassifyRetry(string? reason, string? message)
    {
        var combined = string.Join(' ', new[] { reason, message }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var info = Classify(null, combined, null);
        return info.Class == ErrorClass.Unknown
            ? info with { Name = "Retry" }
            : info;
    }
}
