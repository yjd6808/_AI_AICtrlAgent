using AiCtrl.Client.Commands;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Commands;

public sealed class AbortAndPromptRunnerTests
{
    private static readonly SessionRef Session = new(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_1");

    [Fact]
    public async Task Busy_session_is_aborted_then_prompted()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_1", AcRunState.Busy);

        var result = await CreateRunner().RunAsync(instance, Payload("hello"), "cmd_1", ct);

        result.Status.ShouldBe(CommandStatus.Succeeded);
        instance.Aborted.ShouldBe(new[] { "ses_1" });
        instance.Prompts.Count.ShouldBe(1);
        instance.Prompts[0].ClientMessageId.ShouldBe("cmd_1");
    }

    [Fact]
    public async Task Abort_is_retried_when_first_attempt_is_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_1", AcRunState.Busy);
        instance.OnAbort = (sessionId, count) =>
        {
            if (count == 1)
            {
                return ValueTask.FromResult(new AbortResult(false, "ignored"));
            }

            instance.SetState(sessionId, AcRunState.Idle);
            return ValueTask.FromResult(new AbortResult(true, null));
        };

        var result = await CreateRunner(abortWait: TimeSpan.FromMilliseconds(40))
            .RunAsync(instance, Payload("hello"), "cmd_2", ct);

        result.Status.ShouldBe(CommandStatus.Succeeded);
        instance.Aborted.Count.ShouldBe(2);
        instance.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Abort_timeout_fails_without_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_1", AcRunState.Busy);
        instance.OnAbort = (_, _) => ValueTask.FromResult(new AbortResult(false, "ignored"));

        var result = await CreateRunner(
                abortWait: TimeSpan.FromMilliseconds(40),
                abortRetryExtra: TimeSpan.FromMilliseconds(40))
            .RunAsync(instance, Payload("hello"), "cmd_3", ct);

        result.Status.ShouldBe(CommandStatus.Failed);
        result.ErrorCode.ShouldBe(AcErrorCodes.AbortTimeout);
        instance.Aborted.Count.ShouldBe(2);
        instance.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Foreign_busy_session_is_not_aborted()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_1", AcRunState.ForeignBusy);

        var result = await CreateRunner().RunAsync(instance, Payload("hello"), "cmd_4", ct);

        result.Status.ShouldBe(CommandStatus.Failed);
        result.ErrorCode.ShouldBe(AcErrorCodes.ForeignTurn);
        instance.Aborted.ShouldBeEmpty();
        instance.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Busy_children_are_aborted_before_prompting()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_parent", AcRunState.Idle);
        instance.AddSession("ses_child", AcRunState.Busy, parentId: "ses_parent");
        var payload = new SessionAbortAndPromptPayload(
            new SessionRef(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_parent"),
            "hello",
            null);

        var result = await CreateRunner().RunAsync(instance, payload, "cmd_5", ct);

        result.Status.ShouldBe(CommandStatus.Succeeded);
        instance.Aborted.ShouldContain("ses_child");
        instance.Prompts.Count.ShouldBe(1);
        result.Result.ShouldNotBeNull();
        result.Result.Value.GetProperty("abortedSessions").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Unconfirmed_prompt_is_unknown_and_not_resent()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_1", AcRunState.Idle);
        instance.AcceptPrompt = _ => false;

        var result = await CreateRunner(confirmWait: TimeSpan.FromMilliseconds(60))
            .RunAsync(instance, Payload("hello"), "cmd_6", ct);

        result.Status.ShouldBe(CommandStatus.Unknown);
        result.ErrorCode.ShouldBe(AcErrorCodes.PromptUnconfirmed);
        instance.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unfinished_assistant_turn_fails_with_not_settled()
    {
        var ct = TestContext.Current.CancellationToken;
        var instance = new ScriptedProviderInstance();
        instance.AddSession("ses_1", AcRunState.Idle);
        instance.AddAssistantMessage("ses_1", completed: false);

        var result = await CreateRunner().RunAsync(instance, Payload("hello"), "cmd_7", ct);

        result.Status.ShouldBe(CommandStatus.Failed);
        result.ErrorCode.ShouldBe(AcErrorCodes.NotSettled);
        instance.Prompts.ShouldBeEmpty();
    }

    private static SessionAbortAndPromptPayload Payload(string text) => new(Session, text, null);

    private static AbortAndPromptRunner CreateRunner(
        TimeSpan? abortWait = null,
        TimeSpan? abortRetryExtra = null,
        TimeSpan? confirmWait = null) =>
        new(
            new AbortAndPromptOptions(
                TimeSpan.FromMilliseconds(10),
                abortWait ?? TimeSpan.FromMilliseconds(120),
                abortRetryExtra ?? TimeSpan.FromMilliseconds(80),
                confirmWait ?? TimeSpan.FromMilliseconds(80)),
            TimeProvider.System,
            NullLogger<AbortAndPromptRunner>.Instance);
}
