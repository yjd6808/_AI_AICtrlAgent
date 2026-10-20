using AiCtrl.Core.Domain;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;

namespace AiCtrl.Server.Projection;

public static class MachineViewFactory
{
    public static MachineInfo Create(MachineEntity machine, ConnectionRegistry connections)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(connections);

        return new MachineInfo(
            new MachineId(machine.Id),
            machine.Hostname,
            machine.Os,
            machine.ClientVersion,
            connections.GetConnection(machine.Id) is not null,
            machine.LastSeenAt ?? machine.EnrolledAt,
            machine.ObservedIp,
            machine.ObservedCountry,
            "server",
            null,
            Array.Empty<InstanceInfo>(),
            Array.Empty<WorkspaceInfo>(),
            null,
            false,
            Array.Empty<MachineFeature>());
    }
}
