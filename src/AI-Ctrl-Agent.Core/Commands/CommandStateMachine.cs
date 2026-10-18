namespace AiCtrl.Core.Commands;

public static class CommandStateMachine
{
    private static readonly Dictionary<CommandStatus, CommandStatus[]> AllowedTransitions = new()
    {
        [CommandStatus.Created] = [CommandStatus.Queued, CommandStatus.Cancelled],
        [CommandStatus.Queued] = [CommandStatus.Dispatched, CommandStatus.Cancelled, CommandStatus.Expired],
        [CommandStatus.Dispatched] = [CommandStatus.Acknowledged, CommandStatus.Queued, CommandStatus.Cancelled],
        [CommandStatus.Acknowledged] = [CommandStatus.Running, CommandStatus.Cancelled, CommandStatus.Failed, CommandStatus.Unknown],
        [CommandStatus.Running] = [CommandStatus.Succeeded, CommandStatus.Failed, CommandStatus.Cancelled, CommandStatus.Unknown],
        [CommandStatus.Succeeded] = [],
        [CommandStatus.Failed] = [],
        [CommandStatus.Cancelled] = [],
        [CommandStatus.Expired] = [],
        [CommandStatus.Unknown] = [],
    };

    public static bool IsTerminal(CommandStatus status) => AllowedTransitions[status].Length == 0;

    public static bool CanTransition(CommandStatus from, CommandStatus to) =>
        AllowedTransitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static void EnsureTransition(CommandStatus from, CommandStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"허용되지 않는 Command 상태 전이: {from} → {to}");
        }
    }
}
