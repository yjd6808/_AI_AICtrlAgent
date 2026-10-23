using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AiCtrl.Client.Vpn;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Runtime;
using AiCtrl.Tests.Systems;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class VpnRecoverySystemTests
{
    [Fact]
    public void Options_read_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["vpn:recovery:enabled"] = "true",
                ["vpn:recovery:discordWebhookUrl"] = "https://discord.com/api/webhooks/1/token",
                ["vpn:recovery:autoResume"] = "false",
                ["vpn:recovery:country"] = "us",
                ["vpn:recovery:city"] = " Tokyo ",
                ["vpn:recovery:servers:0"] = "JP#1",
                ["vpn:recovery:cooldownSeconds"] = "30",
                ["vpn:recovery:maxAttemptsPerSession"] = "2",
                ["vpn:recovery:continuationPrompt"] = "continue",
            })
            .Build();

        var options = VpnRecoveryOptions.FromConfiguration(configuration);

        options.Enabled.ShouldBeTrue();
        options.AutoResume.ShouldBeFalse();
        options.Country.ShouldBe("US");
        options.City.ShouldBe("Tokyo");
        options.Servers.ShouldBe(["JP#1"]);
        options.Cooldown.ShouldBe(TimeSpan.FromSeconds(30));
        options.MaxAttemptsPerSession.ShouldBe(2);
        options.ContinuationPrompt.ShouldBe("continue");
        options.WebhookUri.ShouldNotBeNull();
    }

    [Fact]
    public async Task Quota_event_connects_vpn_and_resumes_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await RecoveryHarness.StartAsync(
            new VpnRecoveryOptions
            {
                Enabled = true,
                Cooldown = TimeSpan.FromMinutes(10),
            },
            ct);

        await harness.PublishQuotaAsync(ct);
        var resumed = await harness.Notifier.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        resumed.Kind.ShouldBe(VpnRecoveryNoticeKind.Resumed);
        harness.Actions.AbortCalls.ShouldBe(1);
        harness.Actions.ConnectCalls.ShouldBe(1);
        harness.Actions.ResumeCalls.ShouldBe(1);
        harness.Notifier.Kinds().ShouldContain(VpnRecoveryNoticeKind.QuotaExceeded);
        harness.Notifier.Kinds().ShouldContain(VpnRecoveryNoticeKind.VpnConnected);
        harness.Notifier.Kinds().ShouldContain(VpnRecoveryNoticeKind.Resumed);
    }

    [Fact]
    public async Task Network_retry_event_passes_retry_info_to_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await RecoveryHarness.StartAsync(
            new VpnRecoveryOptions { Enabled = true },
            ct);
        var nextAt = harness.Time.GetUtcNow().AddMinutes(2);

        await harness.PublishNetworkRetryAsync(nextAt, ct);
        await harness.Notifier.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        harness.Notifier.Kinds().ShouldContain(VpnRecoveryNoticeKind.Stalled);
        harness.Actions.LastContext.ShouldNotBeNull();
        harness.Actions.LastContext.ErrorClass.ShouldBe(ErrorClass.Network);
        harness.Actions.LastContext.Retry.ShouldNotBeNull();
        harness.Actions.LastContext.Retry.Attempt.ShouldBe(2);
        harness.Actions.LastContext.Retry.NextAt.ShouldBe(nextAt);
    }

    [Fact]
    public async Task Stall_event_starts_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await RecoveryHarness.StartAsync(
            new VpnRecoveryOptions { Enabled = true },
            ct);

        await harness.PublishStallAsync(TimeSpan.FromMinutes(12), ct);
        var stalled = await harness.Notifier.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        stalled.Kind.ShouldBe(VpnRecoveryNoticeKind.Stalled);
        stalled.StalledFor.ShouldBe(TimeSpan.FromMinutes(12));
        harness.Actions.AbortCalls.ShouldBe(1);
        harness.Actions.ResumeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Attempt_limit_notifies_once_and_does_not_reconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await RecoveryHarness.StartAsync(
            new VpnRecoveryOptions
            {
                Enabled = true,
                Cooldown = TimeSpan.Zero,
                MaxAttemptsPerSession = 1,
            },
            ct);

        await harness.PublishQuotaAsync(ct);
        await harness.Notifier.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await harness.PublishQuotaAsync(ct);
        var exhausted = await harness.Notifier.Exhausted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        exhausted.Kind.ShouldBe(VpnRecoveryNoticeKind.Exhausted);
        harness.Actions.ConnectCalls.ShouldBe(1);
        harness.Actions.ResumeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Connect_failure_skips_resume_and_reports_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await RecoveryHarness.StartAsync(
            new VpnRecoveryOptions { Enabled = true },
            ct,
            connectResult: VpnRecoveryActionResult.Failure("vpn_signin_required"));

        await harness.PublishQuotaAsync(ct);
        var failed = await harness.Notifier.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        failed.ErrorCode.ShouldBe("vpn_signin_required");
        harness.Actions.AbortCalls.ShouldBe(1);
        harness.Actions.ResumeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Discord_notifier_posts_content_without_mentions()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var notifier = new DiscordVpnRecoveryNotifier(
            new VpnRecoveryOptions
            {
                DiscordWebhookUrl = "https://discord.com/api/webhooks/1/token",
            },
            NullLogger<DiscordVpnRecoveryNotifier>.Instance,
            client);
        var session = new SessionRef(
            new MachineId("m_test"),
            new InstanceId("opencode:test"),
            "ses_1");

        await notifier.SendAsync(
            new VpnRecoveryNotice(VpnRecoveryNoticeKind.QuotaExceeded, "machine", session),
            TestContext.Current.CancellationToken);

        handler.Request.ShouldNotBeNull();
        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri.ShouldBe(new Uri("https://discord.com/api/webhooks/1/token"));
        using var document = JsonDocument.Parse(handler.Body!);
        var content = document.RootElement.GetProperty("content").GetString() ?? string.Empty;
        content.ShouldContain("ses_1");
        document.RootElement
            .GetProperty("allowed_mentions")
            .GetProperty("parse")
            .GetArrayLength()
            .ShouldBe(0);
    }

    [Fact]
    public async Task Discord_notifier_failure_does_not_expose_webhook_url()
    {
        var handler = new CaptureHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
        };
        using var client = new HttpClient(handler);
        var notifier = new DiscordVpnRecoveryNotifier(
            new VpnRecoveryOptions
            {
                DiscordWebhookUrl = "https://discord.com/api/webhooks/1/secret-token",
            },
            NullLogger<DiscordVpnRecoveryNotifier>.Instance,
            client);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => notifier.SendAsync(
                new VpnRecoveryNotice(
                    VpnRecoveryNoticeKind.Failed,
                    "machine",
                    new SessionRef(
                        new MachineId("m_test"),
                        new InstanceId("opencode:test"),
                        "ses_1")),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("secret-token");
    }

    [Fact]
    public async Task Discord_notifier_ignores_empty_webhook()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var notifier = new DiscordVpnRecoveryNotifier(
            new VpnRecoveryOptions(),
            NullLogger<DiscordVpnRecoveryNotifier>.Instance,
            client);

        await notifier.SendAsync(
            new VpnRecoveryNotice(
                VpnRecoveryNoticeKind.Stalled,
                "machine",
                new SessionRef(
                    new MachineId("m_test"),
                    new InstanceId("opencode:test"),
                    "ses_1"),
                StalledFor: TimeSpan.FromMinutes(10)),
            TestContext.Current.CancellationToken);

        handler.Request.ShouldBeNull();
    }

    private sealed class RecoveryHarness : IAsyncDisposable
    {
        private readonly ChannelAcEventBus _bus;
        private readonly AcLoopSupervisor _loops;
        private readonly VpnRecoverySystem _system;
        private readonly SessionRef _session = new(
            new MachineId("m_test"),
            new InstanceId("opencode:test"),
            "ses_1");

        private RecoveryHarness(
            ChannelAcEventBus bus,
            AcLoopSupervisor loops,
            VpnRecoverySystem system,
            RecordingActions actions,
            RecordingNotifier notifier,
            FakeTimeProvider time)
        {
            _bus = bus;
            _loops = loops;
            _system = system;
            Actions = actions;
            Notifier = notifier;
            Time = time;
        }

        public RecordingActions Actions { get; }

        public RecordingNotifier Notifier { get; }

        public FakeTimeProvider Time { get; }

        public static async Task<RecoveryHarness> StartAsync(
            VpnRecoveryOptions options,
            CancellationToken ct,
            VpnRecoveryActionResult? connectResult = null)
        {
            var logs = new CapturingLoggerFactory();
            var bus = new ChannelAcEventBus(logs);
            var time = new FakeTimeProvider();
            var loops = new AcLoopSupervisor(time, logs);
            var actions = new RecordingActions(
                connectResult ?? VpnRecoveryActionResult.Success("JP#2"));
            var notifier = new RecordingNotifier();
            var system = new VpnRecoverySystem(
                actions,
                notifier,
                options,
                logs.CreateLogger<VpnRecoverySystem>());
            var context = new AcSystemContext(
                new ServiceCollection().BuildServiceProvider(),
                bus,
                time,
                logs,
                loops);
            await system.InitAsync(context, ct);
            await system.StartAsync(ct);
            return new RecoveryHarness(bus, loops, system, actions, notifier, time);
        }

        public ValueTask PublishQuotaAsync(CancellationToken ct) =>
            _bus.PublishAsync(
                new SessionErrorClassifiedEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    OccurredAt = Time.GetUtcNow(),
                    Session = _session,
                    Error = new ErrorInfo(
                        ErrorClass.QuotaExceeded,
                        "quota",
                        "quota exhausted",
                        402,
                        null),
                },
                ct);

        public ValueTask PublishNetworkRetryAsync(DateTimeOffset nextAt, CancellationToken ct) =>
            _bus.PublishAsync(
                new SessionErrorClassifiedEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    OccurredAt = Time.GetUtcNow(),
                    Session = _session,
                    Error = new ErrorInfo(
                        ErrorClass.Network,
                        "retry",
                        "fetch failed",
                        null,
                        null),
                    Retry = new RetryInfo(2, "fetch failed", nextAt),
                },
                ct);

        public ValueTask PublishStallAsync(TimeSpan stalledFor, CancellationToken ct) =>
            _bus.PublishAsync(
                new SessionStalledEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    OccurredAt = Time.GetUtcNow(),
                    Session = _session,
                    StalledFor = stalledFor,
                },
                ct);

        public async ValueTask DisposeAsync()
        {
            await _system.StopAsync(CancellationToken.None);
            await _loops.DisposeAsync();
        }
    }

    private sealed class RecordingActions : IVpnRecoveryActions
    {
        private readonly VpnRecoveryActionResult _connectResult;

        public RecordingActions(VpnRecoveryActionResult connectResult)
        {
            _connectResult = connectResult;
        }

        public int AbortCalls { get; private set; }

        public int ConnectCalls { get; private set; }

        public int ResumeCalls { get; private set; }

        public VpnRecoveryContext? LastContext { get; private set; }

        public Task<VpnRecoveryActionResult> AbortSessionAsync(
            SessionRef session,
            CancellationToken ct)
        {
            AbortCalls++;
            return Task.FromResult(VpnRecoveryActionResult.Success());
        }

        public Task<VpnRecoveryActionResult> ConnectVpnAsync(
            VpnRecoveryContext context,
            CancellationToken ct)
        {
            ConnectCalls++;
            LastContext = context;
            return Task.FromResult(_connectResult);
        }

        public Task<VpnRecoveryActionResult> ResumeSessionAsync(
            SessionRef session,
            CancellationToken ct)
        {
            ResumeCalls++;
            return Task.FromResult(VpnRecoveryActionResult.Success());
        }
    }

    private sealed class RecordingNotifier : IVpnRecoveryNotifier
    {
        private readonly ConcurrentQueue<VpnRecoveryNotice> _notices = new();

        public TaskCompletionSource<VpnRecoveryNotice> Resumed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<VpnRecoveryNotice> Stalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<VpnRecoveryNotice> Exhausted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<VpnRecoveryNotice> Failed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(VpnRecoveryNotice notice, CancellationToken ct)
        {
            _notices.Enqueue(notice);
            switch (notice.Kind)
            {
                case VpnRecoveryNoticeKind.Resumed:
                    Resumed.TrySetResult(notice);
                    break;
                case VpnRecoveryNoticeKind.Stalled:
                    Stalled.TrySetResult(notice);
                    break;
                case VpnRecoveryNoticeKind.Exhausted:
                    Exhausted.TrySetResult(notice);
                    break;
                case VpnRecoveryNoticeKind.Failed:
                    Failed.TrySetResult(notice);
                    break;
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<VpnRecoveryNoticeKind> Kinds() =>
            _notices.Select(notice => notice.Kind).ToArray();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusCode);
        }
    }
}
