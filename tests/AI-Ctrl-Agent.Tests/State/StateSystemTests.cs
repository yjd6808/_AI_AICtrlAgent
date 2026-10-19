using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Runtime;
using AiCtrl.Tests.Runtime;
using AiCtrl.Tests.Systems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.State;

public sealed class StateSystemTests
{
    private static readonly SessionRef Session = new(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_1");

    [Fact]
    public async Task Tracks_busy_permission_and_completion_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var logs = new CapturingLoggerFactory();
        var bus = new ChannelAcEventBus(logs);
        var loops = new AcLoopSupervisor(time, logs);
        var state = new StateSystem(time, new MonitorOptions(), logs);
        var ctx = new AcSystemContext(new ServiceCollection().BuildServiceProvider(), bus, time, logs, loops);

        await state.InitAsync(ctx, ct);

        await bus.PublishAsync(Status(now, AcRunState.Busy), ct);
        await RuntimeTestHelpers.WaitUntilAsync(() => state.GetSnapshot(Session)?.DisplayState == SessionDisplayState.Running);

        await bus.PublishAsync(
            new SessionPermissionRequestedEvent
            {
                EventId = "evt_permission",
                OccurredAt = now,
                Session = Session,
                PermissionId = "perm_1",
                Permission = "bash",
                Patterns = Array.Empty<string>(),
            },
            ct);
        await RuntimeTestHelpers.WaitUntilAsync(() => state.GetSnapshot(Session)?.DisplayState == SessionDisplayState.WaitingPermission);

        await bus.PublishAsync(
            new SessionTurnCompletedEvent
            {
                EventId = "evt_completed",
                OccurredAt = now,
                Session = Session,
                MessageId = "msg_1",
                Duration = TimeSpan.FromSeconds(1),
            },
            ct);
        await bus.PublishAsync(Status(now, AcRunState.Idle), ct);
        await RuntimeTestHelpers.WaitUntilAsync(() =>
        {
            var snapshot = state.GetSnapshot(Session);
            return snapshot?.DisplayState == SessionDisplayState.Idle && snapshot.Retry is null;
        });

        var final = state.GetSnapshot(Session);
        final.ShouldNotBeNull();
        final.RunState.ShouldBe(AcRunState.Idle);

        await state.StopAsync(ct);
        await loops.DisposeAsync();
    }

    private static SessionStatusChangedEvent Status(DateTimeOffset now, AcRunState runState) =>
        new()
        {
            EventId = $"evt_{runState}",
            OccurredAt = now,
            Session = Session,
            RunState = runState,
        };
}
