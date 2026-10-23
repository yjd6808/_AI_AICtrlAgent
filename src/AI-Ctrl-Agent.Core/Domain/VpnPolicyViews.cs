namespace AiCtrl.Core.Domain;

public static class VpnServerStatuses
{
    public const string Ok = "ok";

    public const string QuotaExhausted = "quota_exhausted";

    public const string OpenCodeTimeout = "opencode_timeout";

    public const string ConnectFailed = "connect_failed";
}

public sealed record VpnServerPolicyStateView(
    string Server,
    string Status,
    string? Country,
    string? City,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? AvailableAt,
    string? LastExitIp,
    int FailureCount,
    string? LastError);

public sealed record VpnPolicyView(
    bool Enabled,
    int MinLoad,
    int MaxLoad,
    int ExcludeUsedWithinHours,
    int MaxAttemptsPerSession,
    int RetryWaitThresholdSeconds,
    IReadOnlyList<string> Countries,
    string? CurrentServer,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<VpnServerPolicyStateView> Servers);

public sealed record VpnPolicyUpdatePayload(
    bool Enabled,
    int MinLoad,
    int MaxLoad,
    int ExcludeUsedWithinHours,
    int MaxAttemptsPerSession,
    int RetryWaitThresholdSeconds,
    IReadOnlyList<string> Countries);
