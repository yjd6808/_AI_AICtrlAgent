using AiCtrl.Core.Commands;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Commands;

public sealed class CommandStateMachineTests
{
    [Theory]
    [InlineData(CommandStatus.Created, CommandStatus.Queued)]
    [InlineData(CommandStatus.Created, CommandStatus.Cancelled)]
    [InlineData(CommandStatus.Queued, CommandStatus.Dispatched)]
    [InlineData(CommandStatus.Queued, CommandStatus.Expired)]
    [InlineData(CommandStatus.Dispatched, CommandStatus.Acknowledged)]
    [InlineData(CommandStatus.Dispatched, CommandStatus.Queued)]
    [InlineData(CommandStatus.Acknowledged, CommandStatus.Running)]
    [InlineData(CommandStatus.Acknowledged, CommandStatus.Failed)]
    [InlineData(CommandStatus.Acknowledged, CommandStatus.Unknown)]
    [InlineData(CommandStatus.Running, CommandStatus.Succeeded)]
    [InlineData(CommandStatus.Running, CommandStatus.Failed)]
    [InlineData(CommandStatus.Running, CommandStatus.Cancelled)]
    [InlineData(CommandStatus.Running, CommandStatus.Unknown)]
    public void Allowed_transitions(CommandStatus from, CommandStatus to)
    {
        CommandStateMachine.CanTransition(from, to).ShouldBeTrue();
        CommandStateMachine.EnsureTransition(from, to);
    }

    [Theory]
    [InlineData(CommandStatus.Created, CommandStatus.Running)]
    [InlineData(CommandStatus.Created, CommandStatus.Dispatched)]
    [InlineData(CommandStatus.Queued, CommandStatus.Running)]
    [InlineData(CommandStatus.Dispatched, CommandStatus.Succeeded)]
    [InlineData(CommandStatus.Acknowledged, CommandStatus.Queued)]
    [InlineData(CommandStatus.Running, CommandStatus.Queued)]
    [InlineData(CommandStatus.Succeeded, CommandStatus.Running)]
    public void Forbidden_transitions(CommandStatus from, CommandStatus to)
    {
        CommandStateMachine.CanTransition(from, to).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => CommandStateMachine.EnsureTransition(from, to));
    }

    [Theory]
    [InlineData(CommandStatus.Succeeded)]
    [InlineData(CommandStatus.Failed)]
    [InlineData(CommandStatus.Cancelled)]
    [InlineData(CommandStatus.Expired)]
    [InlineData(CommandStatus.Unknown)]
    public void Terminal_states_have_no_outgoing_transitions(CommandStatus status)
    {
        CommandStateMachine.IsTerminal(status).ShouldBeTrue();
        foreach (var target in Enum.GetValues<CommandStatus>())
        {
            CommandStateMachine.CanTransition(status, target).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(CommandStatus.Created)]
    [InlineData(CommandStatus.Queued)]
    [InlineData(CommandStatus.Dispatched)]
    [InlineData(CommandStatus.Acknowledged)]
    [InlineData(CommandStatus.Running)]
    public void Non_terminal_states_have_outgoing_transitions(CommandStatus status)
    {
        CommandStateMachine.IsTerminal(status).ShouldBeFalse();
    }
}
