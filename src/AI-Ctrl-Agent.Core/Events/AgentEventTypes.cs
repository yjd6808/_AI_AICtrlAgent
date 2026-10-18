namespace AiCtrl.Core.Events;

public static class AgentEventTypes
{
    public const string MachineState = "machine.state";

    public const string MachineHealth = "machine.health";

    public const string MachineFeatures = "machine.features";

    public const string MachineSessionLogoff = "machine.session.logoff";

    public const string InstanceUpserted = "instance.upserted";

    public const string InstanceRemoved = "instance.removed";

    public const string InstanceHealth = "instance.health";

    public const string InstanceDiscovered = "instance.discovered";

    public const string SessionUpserted = "session.upserted";

    public const string SessionStatus = "session.status";

    public const string SessionErrorClassified = "session.error.classified";

    public const string SessionTurnCompleted = "session.turn.completed";

    public const string SessionStalled = "session.stalled";

    public const string SessionPermissionRequested = "session.permission.requested";

    public const string MessageUpserted = "message.upserted";

    public const string MessagePartsBatched = "message.parts.batched";

    public const string VpnState = "vpn.state";

    public const string CommandProgress = "command.progress";

    public const string WorkspaceCandidate = "workspace.candidate";

    public const string TransferProgress = "transfer.progress";

    public const string ConfigInvalid = "config.invalid";
}
