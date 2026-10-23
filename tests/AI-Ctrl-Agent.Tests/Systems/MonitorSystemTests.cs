using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Providers;
using AiCtrl.Tests.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Systems;

public sealed class MonitorSystemTests
{
    private const string Session = "ses_1";

    [Fact]
    public async Task Provider_events_are_published_to_the_broker()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new ProviderEventBroker(NullLogger<ProviderEventBroker>.Instance);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = events.Subscribe((_, providerEvent) => received.TrySetResult(providerEvent.Kind));
        await using var harness = await MonitorHarness.StartAsync(ct, events: events);

        harness.Instance.Emit("session.idle", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
        });

        var kind = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        kind.ShouldBe("session.idle");
    }

    [Fact]
    public async Task Busy_to_idle_with_completed_assistant_raises_turn_completed_and_logs_ai_output()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct);
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Emit("message.updated", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["info"] = new Dictionary<string, object?>
            {
                ["id"] = "msg_u1",
                ["role"] = "user",
                ["time"] = new Dictionary<string, object?> { ["created"] = 1000L },
            },
        });
        harness.Instance.Emit("message.part.updated", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = "prt_u1",
                ["messageID"] = "msg_u1",
                ["type"] = "text",
                ["text"] = "please refactor",
                ["time"] = new Dictionary<string, object?> { ["start"] = 1000L },
            },
        });
        harness.Instance.Emit("session.status", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["status"] = new Dictionary<string, object?> { ["type"] = "busy" },
        });
        harness.Instance.Emit("message.updated", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["info"] = new Dictionary<string, object?>
            {
                ["id"] = "msg_a1",
                ["role"] = "assistant",
                ["modelID"] = "fake-model",
                ["providerID"] = "opencode",
                ["time"] = new Dictionary<string, object?> { ["created"] = 1100L },
            },
        });
        harness.Instance.Emit("message.part.updated", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = "prt_a1",
                ["messageID"] = "msg_a1",
                ["type"] = "text",
                ["text"] = "done refactoring",
                ["time"] = new Dictionary<string, object?> { ["start"] = 1100L, ["end"] = 1200L },
            },
        });
        harness.Instance.Messages.Add(new MessageView(
            "msg_a1",
            "assistant",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new ModelRef("opencode", "fake-model"),
            new[]
            {
                new PartView("prt_a1", "text", "done refactoring", null),
                new PartView("prt_a2", "reasoning", "thought about it", null),
                new PartView("prt_a3", "tool", null, new ToolPartView("bash", "completed", "Run tests", "OUT ok", "{\"cmd\":\"dotnet test\"}", null, null)),
            },
            null));
        harness.Instance.Emit("message.updated", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["info"] = new Dictionary<string, object?>
            {
                ["id"] = "msg_a1",
                ["role"] = "assistant",
                ["modelID"] = "fake-model",
                ["providerID"] = "opencode",
                ["time"] = new Dictionary<string, object?> { ["created"] = 1100L, ["completed"] = 1200L },
            },
        });
        harness.Instance.Emit("session.status", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["status"] = new Dictionary<string, object?> { ["type"] = "idle" },
        });

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionTurnCompletedEvent>() is not null,
            TimeSpan.FromSeconds(5));

        var completed = collector.First<SessionTurnCompletedEvent>()!;
        completed.MessageId.ShouldBe("msg_a1");
        completed.Session.SessionId.ShouldBe(Session);

        var statuses = collector.Events.OfType<SessionStatusChangedEvent>().Select(e => e.RunState).ToList();
        statuses.ShouldContain(AcRunState.Busy);
        statuses.ShouldContain(AcRunState.Idle);

        await RuntimeTestHelpers.WaitUntilAsync(
            () => harness.Logs.HasMessage("AiOutput", "[reasoning]") && harness.Logs.HasMessage("AiOutput", "[tool bash completed]"),
            TimeSpan.FromSeconds(5));

        harness.Logs.HasMessage("AiOutput", "[user prompt]").ShouldBeTrue();
        harness.Logs.HasMessage("AiOutput", "[assistant text]").ShouldBeTrue();
        harness.Logs.Entries.ShouldContain(entry => entry.Category == "AiOutput" && entry.Message.Contains("done refactoring", StringComparison.Ordinal));
        harness.Logs.Entries.ShouldContain(entry => entry.Category == "AiOutput" && entry.Message.Contains("thought about it", StringComparison.Ordinal));
        harness.Logs.Entries.ShouldContain(entry => entry.Category == "AiOutput" && entry.Message.Contains("OUT ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Busy_session_without_events_raises_stalled_once_after_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct, new MonitorOptions
        {
            StallCheckInterval = TimeSpan.FromSeconds(10),
            StallThreshold = TimeSpan.FromSeconds(30),
        });
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Emit("session.status", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["status"] = new Dictionary<string, object?> { ["type"] = "busy" },
        });

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionStatusChangedEvent>() is not null,
            TimeSpan.FromSeconds(5));

        harness.Time.Advance(TimeSpan.FromSeconds(30));

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionStalledEvent>() is not null,
            TimeSpan.FromSeconds(5));

        var stalled = collector.First<SessionStalledEvent>()!;
        stalled.Session.SessionId.ShouldBe(Session);
        stalled.StalledFor.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));

        harness.Time.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(100, ct);

        collector.Events.OfType<SessionStalledEvent>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Retry_with_quota_reason_raises_classified_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct);
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Emit("session.status", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["status"] = new Dictionary<string, object?>
            {
                ["type"] = "retry",
                ["attempt"] = 2,
                ["message"] = "Free usage limit reached",
                ["next"] = 1_700_000_000_000L,
                ["action"] = new Dictionary<string, object?> { ["reason"] = "quota" },
            },
        });

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionErrorClassifiedEvent>() is not null,
            TimeSpan.FromSeconds(5));

        var classified = collector.First<SessionErrorClassifiedEvent>()!;
        classified.Error.Class.ShouldBe(ErrorClass.QuotaExceeded);

        var status = collector.Events.OfType<SessionStatusChangedEvent>().FirstOrDefault();
        status.ShouldNotBeNull();
        status.RunState.ShouldBe(AcRunState.Retrying);
        status.Retry.ShouldNotBeNull();
        status.Retry.Attempt.ShouldBe(2);

        harness.Logs.HasMessage("AiOutput", "[error QuotaExceeded]").ShouldBeTrue();
    }

    [Fact]
    public async Task Retry_with_network_reason_raises_classified_error_with_retry_info()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct);
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Emit("session.status", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["status"] = new Dictionary<string, object?>
            {
                ["type"] = "retry",
                ["attempt"] = 3,
                ["message"] = "fetch failed",
                ["next"] = 1_700_000_000_000L,
                ["action"] = new Dictionary<string, object?> { ["reason"] = "timeout" },
            },
        });

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionErrorClassifiedEvent>() is not null,
            TimeSpan.FromSeconds(5));

        var classified = collector.First<SessionErrorClassifiedEvent>()!;
        classified.Error.Class.ShouldBe(ErrorClass.Network);
        classified.Retry.ShouldNotBeNull();
        classified.Retry.Attempt.ShouldBe(3);
        classified.Retry.Message.ShouldBe("fetch failed");
        classified.Retry.NextAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L));
    }

    [Fact]
    public async Task Session_error_event_raises_classified_error_for_rate_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct);
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Emit("session.error", new Dictionary<string, object?>
        {
            ["sessionID"] = Session,
            ["error"] = new Dictionary<string, object?>
            {
                ["name"] = "APIError",
                ["data"] = new Dictionary<string, object?>
                {
                    ["message"] = "rate limit exceeded",
                    ["statusCode"] = 429,
                },
            },
        });

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionErrorClassifiedEvent>() is not null,
            TimeSpan.FromSeconds(5));

        collector.First<SessionErrorClassifiedEvent>()!.Error.Class.ShouldBe(ErrorClass.RateLimited);
        harness.Logs.HasMessage("AiOutput", "[error RateLimited]").ShouldBeTrue();
    }

    [Fact]
    public async Task Permission_asked_event_raises_permission_requested()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct);
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Emit("permission.asked", new Dictionary<string, object?>
        {
            ["id"] = "per_1",
            ["sessionID"] = Session,
            ["permission"] = "bash",
            ["patterns"] = new[] { "git push *" },
        });

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionPermissionRequestedEvent>() is not null,
            TimeSpan.FromSeconds(5));

        var permission = collector.First<SessionPermissionRequestedEvent>()!;
        permission.Session.SessionId.ShouldBe(Session);
        permission.PermissionId.ShouldBe("per_1");
        permission.Permission.ShouldBe("bash");
        permission.Patterns.ShouldBe(new[] { "git push *" });
    }

    [Fact]
    public async Task Snapshot_required_event_reconciles_session_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MonitorHarness.StartAsync(ct);
        await using var collector = new EventCollector(harness.Bus);

        harness.Instance.Sessions.Add(new SessionSummary(
            new SessionRef(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_r"),
            "reconciled",
            null,
            @"C:\ws",
            AcRunState.Busy,
            ErrorClass.None,
            null,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            SessionDisplayState.Running,
            null,
            null,
            null,
            true));

        harness.Instance.Emit(ProviderEventKinds.SnapshotRequired, null);

        await RuntimeTestHelpers.WaitUntilAsync(
            () => collector.First<SessionStatusChangedEvent>()?.RunState == AcRunState.Busy,
            TimeSpan.FromSeconds(5));

        var status = collector.First<SessionStatusChangedEvent>()!;
        status.Session.SessionId.ShouldBe("ses_r");
        status.Session.Machine.Value.ShouldBe("m_test");
        status.Session.Instance.Value.ShouldBe("opencode:test");
    }
}
