using AiCtrl.Core.Domain;

namespace AiCtrl.Client.Vpn;

public static class VpnServerListBuilder
{
    public static VpnServerPage Build(
        VpnCountry country,
        IReadOnlyList<VpnServer> servers,
        VpnStatusView status,
        ConnectionHistory history,
        DateTimeOffset now,
        string? previousCity)
    {
        ArgumentNullException.ThrowIfNull(country);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(history);

        var cityOrder = country.Cities
            .Select((city, index) => (city, index))
            .ToDictionary(pair => pair.city, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        var groups = servers
            .GroupBy(server => server.City ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.Select(server =>
                {
                    var lastConnected = history.GetLastConnected(server.Name);
                    var rowState = string.Equals(server.Name, status.Server, StringComparison.OrdinalIgnoreCase)
                        ? VpnRowState.Connected
                        : ConnectionHistory.EvaluateState(lastConnected, now);
                    return new VpnServerRow(
                        server.Name,
                        server.Country,
                        server.CountryName,
                        server.City,
                        server.Tier,
                        server.TierName,
                        server.Load,
                        server.Score,
                        server.Features,
                        lastConnected,
                        rowState);
                })
                .OrderBy(row => StateRank(row.RowState))
                .ThenBy(row => row.Load)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

                return new VpnCityGroup(
                    group.Key,
                    rows.Length,
                    rows.Any(row => row.RowState is VpnRowState.Connected or VpnRowState.Recent),
                    rows);
            })
            .OrderBy(group => CityRank(group.City, cityOrder))
            .ThenBy(group => group.City, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var suggestedCity = FindSuggestedCity(groups, status, previousCity);
        return new VpnServerPage(country.Code, groups, suggestedCity, now);
    }

    private static string? FindSuggestedCity(
        IReadOnlyList<VpnCityGroup> groups,
        VpnStatusView status,
        string? previousCity)
    {
        if (status.TunnelUp && !string.IsNullOrWhiteSpace(status.City))
        {
            var connected = groups.FirstOrDefault(group => string.Equals(
                group.City,
                status.City,
                StringComparison.OrdinalIgnoreCase));
            if (connected is not null)
            {
                return connected.City;
            }
        }

        if (!string.IsNullOrWhiteSpace(previousCity))
        {
            var previous = groups.FirstOrDefault(group => string.Equals(
                group.City,
                previousCity,
                StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                return previous.City;
            }
        }

        return groups.FirstOrDefault(group => group.Highlight)?.City ?? groups.FirstOrDefault()?.City;
    }

    private static int CityRank(string city, IReadOnlyDictionary<string, int> cityOrder) =>
        cityOrder.TryGetValue(city, out var rank) ? rank : int.MaxValue;

    private static int StateRank(VpnRowState state) => state switch
    {
        VpnRowState.Connected => 0,
        VpnRowState.Recent => 1,
        _ => 2,
    };
}
