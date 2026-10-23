using AiCtrl.Core.Domain;

namespace AiCtrl.Client.Vpn;

public static class VpnSelectionReasons
{
    public const string Selected = "selected";

    public const string NoCandidates = "no_candidates";

    public const string PolicyDisabled = "policy_disabled";

    public const string Country = "country";

    public const string Load = "load";

    public const string Current = "current";

    public const string Avoided = "avoided";

    public const string Cooldown = "cooldown";

    public const string RecentlyUsed = "recently_used";
}

public sealed record VpnSelectionCandidate(
    VpnServer Server,
    bool Eligible,
    string Reason,
    DateTimeOffset? AvailableAt);

public sealed record VpnSelectionResult(
    VpnServer? Selected,
    IReadOnlyList<VpnSelectionCandidate> Candidates,
    DateTimeOffset? NextAvailableAt,
    string Reason);

public static class VpnServerSelector
{
    public static VpnSelectionResult Select(
        VpnPolicyDocument policy,
        IReadOnlyList<VpnServer> servers,
        string? currentServer,
        IReadOnlySet<string>? avoid,
        DateTimeOffset now,
        Func<double>? random = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(servers);
        var normalized = policy.Normalize();
        if (!normalized.Enabled)
        {
            return new VpnSelectionResult(
                null,
                servers.Select(server => new VpnSelectionCandidate(
                    server,
                    false,
                    VpnSelectionReasons.PolicyDisabled,
                    null)).ToArray(),
                null,
                VpnSelectionReasons.PolicyDisabled);
        }

        var candidates = new List<VpnSelectionCandidate>(servers.Count);
        var eligible = new List<VpnServer>();
        DateTimeOffset? nextAvailableAt = null;
        foreach (var server in servers)
        {
            normalized.Servers.TryGetValue(server.Name, out var state);
            var country = !string.IsNullOrWhiteSpace(server.Country)
                ? server.Country
                : ServerCountry(server.Name);
            var availableAt = state?.AvailableAt is { } cooldown && cooldown > now
                ? cooldown
                : (DateTimeOffset?)null;
            if (normalized.Countries.Count > 0
                && !normalized.Countries.Contains(country, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(new VpnSelectionCandidate(server, false, VpnSelectionReasons.Country, availableAt));
                continue;
            }

            if (server.Load < normalized.MinLoad || server.Load > normalized.MaxLoad)
            {
                candidates.Add(new VpnSelectionCandidate(server, false, VpnSelectionReasons.Load, availableAt));
                continue;
            }

            if (state is not null
                && !string.Equals(state.Status, VpnServerStatuses.Ok, StringComparison.Ordinal)
                && availableAt is not null)
            {
                candidates.Add(new VpnSelectionCandidate(server, false, state.Status, availableAt));
                nextAvailableAt = Earliest(nextAvailableAt, availableAt);
                continue;
            }

            if (availableAt is not null)
            {
                candidates.Add(new VpnSelectionCandidate(server, false, VpnSelectionReasons.Cooldown, availableAt));
                nextAvailableAt = Earliest(nextAvailableAt, availableAt);
                continue;
            }

            if (string.Equals(server.Name, currentServer, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new VpnSelectionCandidate(server, false, VpnSelectionReasons.Current, null));
                continue;
            }

            if (avoid is not null && avoid.Contains(server.Name))
            {
                candidates.Add(new VpnSelectionCandidate(server, false, VpnSelectionReasons.Avoided, null));
                continue;
            }

            if (normalized.ExcludeUsedWithinHours > 0
                && state?.LastUsedAt is { } lastUsed
                && lastUsed > now.AddHours(-normalized.ExcludeUsedWithinHours))
            {
                var reusableAt = lastUsed.AddHours(normalized.ExcludeUsedWithinHours);
                candidates.Add(new VpnSelectionCandidate(server, false, VpnSelectionReasons.RecentlyUsed, reusableAt));
                nextAvailableAt = Earliest(nextAvailableAt, reusableAt);
                continue;
            }

            candidates.Add(new VpnSelectionCandidate(server, true, VpnSelectionReasons.Selected, null));
            eligible.Add(server);
        }

        var next = random ?? (() => Random.Shared.NextDouble());
        var selected = eligible
            .OrderBy(server => server.Load)
            .ThenByDescending(server => server.Score)
            .ThenBy(_ => next())
            .FirstOrDefault();
        if (selected is null)
        {
            return new VpnSelectionResult(null, candidates, nextAvailableAt, VpnSelectionReasons.NoCandidates);
        }

        var marked = candidates
            .Select(candidate => string.Equals(
                    candidate.Server.Name,
                    selected.Name,
                    StringComparison.OrdinalIgnoreCase)
                ? candidate with { Eligible = true, Reason = VpnSelectionReasons.Selected }
                : candidate)
            .ToArray();
        return new VpnSelectionResult(selected, marked, nextAvailableAt, VpnSelectionReasons.Selected);
    }

    private static string ServerCountry(string server) =>
        server.Length >= 2 ? server[..2].ToUpperInvariant() : string.Empty;

    private static DateTimeOffset? Earliest(DateTimeOffset? current, DateTimeOffset? candidate) =>
        candidate is null || (current is not null && current <= candidate) ? current : candidate;
}
