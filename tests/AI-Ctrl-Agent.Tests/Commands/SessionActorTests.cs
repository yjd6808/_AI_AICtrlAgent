using System.Collections.Concurrent;
using AiCtrl.Client.Commands;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Commands;

public sealed class SessionActorTests
{
    private static readonly SessionRef SessionA = new(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_a");

    private static readonly SessionRef SessionB = new(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_b");

    [Fact]
    public async Task Prompts_for_same_session_run_sequentially()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var actor = new SessionActor(SessionA, NullLogger.Instance);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;

        var first = actor.ExecuteAsync(CommandTypes.SessionPrompt, async token =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return CommandResult.Success(null);
        }, ct);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var second = actor.ExecuteAsync(CommandTypes.SessionPrompt, _ =>
        {
            secondStarted = true;
            return ValueTask.FromResult(CommandResult.Success(null));
        }, ct);

        await Task.Delay(100, ct);
        secondStarted.ShouldBeFalse();

        releaseFirst.SetResult();
        (await first).Succeeded.ShouldBeTrue();
        (await second).Succeeded.ShouldBeTrue();
        secondStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task Abort_bypasses_queued_prompts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var actor = new SessionActor(SessionA, NullLogger.Instance);
        var events = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = actor.ExecuteAsync(CommandTypes.SessionPrompt, async token =>
        {
            events.Enqueue("prompt1");
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            events.Enqueue("prompt1-end");
            return CommandResult.Success(null);
        }, ct);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var second = actor.ExecuteAsync(CommandTypes.SessionPrompt, _ =>
        {
            events.Enqueue("prompt2");
            return ValueTask.FromResult(CommandResult.Success(null));
        }, ct);

        var abort = actor.ExecuteAsync(CommandTypes.SessionAbort, _ =>
        {
            events.Enqueue("abort");
            return ValueTask.FromResult(CommandResult.Success(null));
        }, ct);

        (await abort).Succeeded.ShouldBeTrue();
        events.ShouldNotContain("prompt2");

        releaseFirst.SetResult();
        (await first).Succeeded.ShouldBeTrue();
        (await second).Succeeded.ShouldBeTrue();

        var ordered = events.ToArray();
        Array.IndexOf(ordered, "abort").ShouldBeLessThan(Array.IndexOf(ordered, "prompt1-end"));
        Array.IndexOf(ordered, "abort").ShouldBeLessThan(Array.IndexOf(ordered, "prompt2"));
    }

    [Fact]
    public async Task Different_sessions_run_in_parallel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = new SessionActorPool(NullLoggerFactory.Instance);
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = pool.ExecuteAsync(SessionA, CommandTypes.SessionPrompt, async token =>
        {
            startedA.SetResult();
            await releaseA.Task.WaitAsync(token);
            return CommandResult.Success(null);
        }, ct);

        var second = pool.ExecuteAsync(SessionB, CommandTypes.SessionPrompt, async token =>
        {
            startedB.SetResult();
            await releaseB.Task.WaitAsync(token);
            return CommandResult.Success(null);
        }, ct);

        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(5), ct);

        releaseA.SetResult();
        releaseB.SetResult();
        (await first).Succeeded.ShouldBeTrue();
        (await second).Succeeded.ShouldBeTrue();
    }
}
