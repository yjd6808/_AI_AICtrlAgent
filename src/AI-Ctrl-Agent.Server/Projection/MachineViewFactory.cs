using System.Net;
using AiCtrl.Core.Domain;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;

namespace AiCtrl.Server.Projection;

public static class MachineViewFactory
{
    public static MachineInfo Create(
        MachineEntity machine,
        ConnectionRegistry connections,
        VpnStatusView? vpn = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(connections);

        var observedIp = machine.ObservedIp;
        var observedIpSource = "server";
        if (UseClientReportedIp(observedIp, vpn?.ExitIp) is { } reported)
        {
            observedIp = reported;
            observedIpSource = "client-reported";
        }

        return new MachineInfo(
            new MachineId(machine.Id),
            machine.Hostname,
            machine.Os,
            machine.ClientVersion,
            connections.GetConnection(machine.Id) is not null,
            machine.LastSeenAt ?? machine.EnrolledAt,
            observedIp,
            machine.ObservedCountry,
            observedIpSource,
            vpn,
            Array.Empty<InstanceInfo>(),
            Array.Empty<WorkspaceInfo>(),
            null,
            false,
            Array.Empty<MachineFeature>());
    }

    private static string? UseClientReportedIp(string? serverIp, string? exitIp)
    {
        if (string.IsNullOrWhiteSpace(exitIp))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(serverIp))
        {
            return exitIp;
        }

        return IPAddress.TryParse(serverIp, out var address) && IPAddress.IsLoopback(address)
            ? exitIp
            : null;
    }
}
