using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Protocol;

public sealed record HelloRequest(
    MachineId MachineId,
    string ClientVersion,
    string BootId,
    long LastAckedEventSeq,
    IReadOnlyList<string> JournalOpenCommandIds,
    int ProtocolVersion = ProtocolVersions.Current);

public sealed record HelloAck(
    long ServerTimeUnixMs,
    string ObservedIp,
    string? ObservedCountry,
    IReadOnlyList<CommandEnvelope> PendingCommands,
    bool SnapshotRequired);

public static class ProtocolVersions
{
    public const int Current = 1;
}
