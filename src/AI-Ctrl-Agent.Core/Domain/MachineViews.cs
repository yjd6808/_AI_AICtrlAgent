using AiCtrl.Core.Providers;

namespace AiCtrl.Core.Domain;

public sealed record MachineInfo(
    MachineId Id,
    string Hostname,
    string Os,
    string ClientVersion,
    bool Online,
    DateTimeOffset LastSeen,
    string? ObservedIp,
    string? ObservedCountry,
    string ObservedIpSource,
    VpnStatusView? Vpn,
    IReadOnlyList<InstanceInfo> Instances,
    IReadOnlyList<WorkspaceInfo> Workspaces,
    MachineHealth? Health,
    bool KeepAwake,
    IReadOnlyList<MachineFeature> Features);

public sealed record InstanceInfo(
    InstanceId Id,
    ProviderKind Kind,
    string Endpoint,
    string? Version,
    InstanceMode Mode,
    bool Healthy,
    bool Managed,
    CapabilitySet Capabilities);

public sealed record MachineHealth(
    double? CpuPercent,
    double? MemoryUsedMb,
    double? MemoryTotalMb,
    double? DiskFreeMb,
    TimeSpan? Uptime,
    double? ClockOffsetMs,
    IReadOnlyDictionary<string, string?> ToolVersions,
    DateTimeOffset AsOf);

public sealed record MachineSnapshot(
    MachineInfo Machine,
    IReadOnlyList<SessionSummary> Sessions);
