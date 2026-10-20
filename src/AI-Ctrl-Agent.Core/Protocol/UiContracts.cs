using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Protocol;

public sealed record FleetSnapshotDto(IReadOnlyList<MachineInfo> Machines);

public interface IUiClient
{
    Task FleetSnapshot(FleetSnapshotDto snapshot);

    Task MachineChanged(MachineInfo machine);

    Task SessionChanged(MachineId machine, SessionSummary session);

    Task MessageUpserted(MachineId machine, string sessionId, MessageView message);

    Task CommandChanged(CommandDetailView command);

    Task StreamChunks(MachineId machine, string sessionId, long epoch, IReadOnlyList<StreamChunk> chunks);

    Task VpnChanged(MachineId machine, VpnStatusView status);
}

public interface IUiHub
{
    Task WatchSession(MachineId machine, string sessionId);

    Task UnwatchSession(MachineId machine, string sessionId);
}
