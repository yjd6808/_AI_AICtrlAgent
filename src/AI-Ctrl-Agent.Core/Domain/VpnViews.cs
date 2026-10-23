namespace AiCtrl.Core.Domain;

public sealed record VpnStatusView(
    bool CliFound,
    string? CliPath,
    bool HelperAvailable,
    bool ProtonGuiRunning,
    bool SignedIn,
    string? Username,
    DateTimeOffset? ExpiresAt,
    bool TunnelUp,
    string? Server,
    string? Country,
    string? City,
    string? Detail,
    string? ExitIp,
    string StatusText,
    string? BusyOperation,
    string? BusyCommandId,
    DateTimeOffset AsOf,
    bool UsesProtonApp = false);

public sealed record VpnCountryView(
    string Code,
    string Name,
    int Count,
    IReadOnlyList<string> Cities);

public enum VpnRowState
{
    Connected,
    Recent,
    Past,
    Never,
}

public sealed record VpnServerRow(
    string Name,
    string Country,
    string CountryName,
    string? City,
    int Tier,
    string TierName,
    int Load,
    double Score,
    IReadOnlyList<string> Features,
    DateTimeOffset? LastConnectedAt,
    VpnRowState RowState);

public sealed record VpnCityGroup(
    string City,
    int Count,
    bool Highlight,
    IReadOnlyList<VpnServerRow> Servers);

public sealed record VpnPageSnapshot(
    VpnStatusView Status,
    IReadOnlyList<VpnCountryView> Countries,
    string SelectedCountry,
    VpnServerPage? Servers,
    string? ActionText);

public sealed record VpnServerPage(
    string Country,
    IReadOnlyList<VpnCityGroup> Cities,
    string? SuggestedCity,
    DateTimeOffset AsOf);

public sealed record VpnConnectInfo(
    string Server,
    string? Country,
    string? CountryName,
    string? City,
    int Load,
    string? ExitIp,
    DateTimeOffset? ConnectedAt);

public sealed record VpnActionSnapshot(
    VpnConnectInfo? Connect,
    string ActionText,
    VpnStatusView Status,
    VpnServerPage? Servers);
