namespace AiCtrl.Core.Domain;

public sealed record VpnServerPolicyState(
    string Status = VpnServerStatuses.Ok,
    string? Country = null,
    string? City = null,
    DateTimeOffset? LastUsedAt = null,
    DateTimeOffset? AvailableAt = null,
    string? LastExitIp = null,
    int FailureCount = 0,
    string? LastError = null);

public sealed record VpnPolicyDocument(
    bool Enabled,
    int MinLoad,
    int MaxLoad,
    int ExcludeUsedWithinHours,
    int MaxAttemptsPerSession,
    int RetryWaitThresholdSeconds,
    IReadOnlyList<string> Countries,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, VpnServerPolicyState> Servers)
{
    public static VpnPolicyDocument CreateDefault(DateTimeOffset now) => new(
        Enabled: true,
        MinLoad: 0,
        MaxLoad: 70,
        ExcludeUsedWithinHours: 24,
        MaxAttemptsPerSession: 3,
        RetryWaitThresholdSeconds: 180,
        Countries: Array.Empty<string>(),
        UpdatedAt: now,
        Servers: new Dictionary<string, VpnServerPolicyState>(StringComparer.OrdinalIgnoreCase));

    public VpnPolicyDocument Normalize()
    {
        var minLoad = Math.Clamp(MinLoad, 0, 100);
        var maxLoad = Math.Clamp(MaxLoad, minLoad, 100);
        var countries = (Countries ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var servers = new Dictionary<string, VpnServerPolicyState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, state) in Servers ?? new Dictionary<string, VpnServerPolicyState>())
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            servers[name.Trim()] = (state ?? new VpnServerPolicyState()) with
            {
                Status = NormalizeStatus(state?.Status),
                FailureCount = Math.Max(0, state?.FailureCount ?? 0),
            };
        }

        return this with
        {
            MinLoad = minLoad,
            MaxLoad = maxLoad,
            ExcludeUsedWithinHours = Math.Clamp(ExcludeUsedWithinHours, 0, 24 * 365),
            MaxAttemptsPerSession = Math.Clamp(MaxAttemptsPerSession, 1, 5),
            RetryWaitThresholdSeconds = Math.Clamp(RetryWaitThresholdSeconds, 1, 24 * 60 * 60),
            Countries = countries,
            Servers = servers,
        };
    }

    public VpnPolicyView ToView(DateTimeOffset now, string? currentServer)
    {
        var normalized = Normalize();
        var rows = normalized.Servers
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new VpnServerPolicyStateView(
                pair.Key,
                pair.Value.Status,
                pair.Value.Country,
                pair.Value.City,
                pair.Value.LastUsedAt,
                pair.Value.AvailableAt is { } availableAt && availableAt > now ? availableAt : null,
                pair.Value.LastExitIp,
                pair.Value.FailureCount,
                pair.Value.LastError))
            .ToArray();
        return new VpnPolicyView(
            normalized.Enabled,
            normalized.MinLoad,
            normalized.MaxLoad,
            normalized.ExcludeUsedWithinHours,
            normalized.MaxAttemptsPerSession,
            normalized.RetryWaitThresholdSeconds,
            normalized.Countries,
            currentServer,
            normalized.UpdatedAt,
            rows);
    }

    private static string NormalizeStatus(string? status) => status?.Trim() switch
    {
        VpnServerStatuses.QuotaExhausted => VpnServerStatuses.QuotaExhausted,
        VpnServerStatuses.OpenCodeTimeout => VpnServerStatuses.OpenCodeTimeout,
        VpnServerStatuses.ConnectFailed => VpnServerStatuses.ConnectFailed,
        _ => VpnServerStatuses.Ok,
    };
}
