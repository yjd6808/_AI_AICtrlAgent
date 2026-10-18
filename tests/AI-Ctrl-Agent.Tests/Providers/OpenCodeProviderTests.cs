using System.Net;
using System.Net.Sockets;
using AiCtrl.Client.Diagnostics;
using AiCtrl.Client.Providers.OpenCode;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.FakeOpenCode;
using AiCtrl.Tests.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Providers;

public sealed class OpenCodeProviderTests : IAsyncLifetime
{
    private static readonly MachineId Machine = new("m_test");

    private FakeOpenCodeServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeOpenCodeServer.StartAsync(
            configure: scenario => scenario.Password = "test-pw",
            ct: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Discover_reports_version_and_capabilities()
    {
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        instance.Info.Version.ShouldBe("1.18.34");
        instance.Info.Healthy.ShouldBeTrue();
        instance.Info.Mode.ShouldBe(InstanceMode.SharedStore);
        instance.Info.Id.Value.ShouldBe(new Uri(_server.BaseUrl).Authority);
        instance.Capabilities.ShouldBe(
            CapabilitySet.ListSessions |
            CapabilitySet.ReadMessages |
            CapabilitySet.Abort |
            CapabilitySet.Prompt |
            CapabilitySet.PromptAsync |
            CapabilitySet.ModelSelect |
            CapabilitySet.Permissions |
            CapabilitySet.LiveEvents |
            CapabilitySet.SubSessions);

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Discover_uses_explicit_instance_id_when_configured()
    {
        var instance = await DiscoverSingleAsync(CreateProvider(instanceId: "opencode-local"), TestContext.Current.CancellationToken);

        instance.Info.Id.Value.ShouldBe("opencode-local");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Discover_skips_unreachable_endpoints()
    {
        var deadPort = GetFreePort();
        var options = new OpenCodeProviderOptions(
            "opencode",
            "test-pw",
            new[] { $"http://127.0.0.1:{deadPort}", _server.BaseUrl });
        var provider = new OpenCodeProvider(Machine, options, new ErrorClassifier(), TimeProvider.System, NullLoggerFactory.Instance);

        var instances = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        instances.Count.ShouldBe(1);
        instances[0].Info.Endpoint.ShouldBe(_server.BaseUrl);

        await instances[0].DisposeAsync();
    }

    [Fact]
    public async Task Discover_reports_unhealthy_instance_for_wrong_password()
    {
        var instance = await DiscoverSingleAsync(CreateProvider(password: "wrong-pw"), TestContext.Current.CancellationToken);

        instance.Info.Healthy.ShouldBeFalse();
        instance.Capabilities.ShouldBe(CapabilitySet.None);

        await instance.DisposeAsync();
    }

    [Theory]
    [InlineData(SessionListViews.Active, false, 1)]
    [InlineData(SessionListViews.Active, true, 1)]
    [InlineData(SessionListViews.All, false, 1)]
    [InlineData(SessionListViews.All, true, 2)]
    public async Task ListSessions_filters_children_by_view(string view, bool includeChildren, int expected)
    {
        var parent = _server.State.SeedSession("parent");
        _server.State.SeedSession("child", parentId: parent);
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var sessions = await instance.ListSessionsAsync(
            new SessionQuery(instance.Info.Id, view, includeChildren, null),
            TestContext.Current.CancellationToken);

        sessions.Count.ShouldBe(expected);
        sessions.ShouldAllBe(summary => (view == SessionListViews.All && includeChildren) || summary.ParentId == null);

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task ListSessions_merges_status_map()
    {
        var session = _server.State.SeedSession("status-check");
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var sessions = await instance.ListSessionsAsync(
            new SessionQuery(instance.Info.Id, SessionListViews.Active, false, null),
            TestContext.Current.CancellationToken);

        var summary = sessions.ShouldHaveSingleItem();
        summary.Ref.SessionId.ShouldBe(session);
        summary.RunState.ShouldBe(AcRunState.Idle);
        summary.ActiveOnInstance.ShouldBeTrue();
        summary.LastModel.ShouldBe(new ModelRef("opencode", "fake-model"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task GetMessages_pages_with_offset_cursor()
    {
        var session = _server.State.SeedSession("paging");
        for (var i = 1; i <= 5; i++)
        {
            _server.State.SeedUserMessage(session, "m" + i);
        }

        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var first = await instance.GetMessagesAsync(session, new PageRequest(null, 2), TestContext.Current.CancellationToken);
        first.Items.Count.ShouldBe(2);
        first.Items[0].Parts[0].Text.ShouldBe("m1");
        first.HasMore.ShouldBeTrue();
        first.NextCursor.ShouldBe("2");

        var second = await instance.GetMessagesAsync(session, new PageRequest(first.NextCursor, 2), TestContext.Current.CancellationToken);
        second.Items.Count.ShouldBe(2);
        second.Items[0].Parts[0].Text.ShouldBe("m3");
        second.HasMore.ShouldBeTrue();
        second.NextCursor.ShouldBe("4");

        var third = await instance.GetMessagesAsync(session, new PageRequest(second.NextCursor, 2), TestContext.Current.CancellationToken);
        third.Items.Count.ShouldBe(1);
        third.Items[0].Parts[0].Text.ShouldBe("m5");
        third.HasMore.ShouldBeFalse();
        third.NextCursor.ShouldBeNull();

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task GetMessages_parses_completed_assistant_message()
    {
        var session = _server.State.SeedSession("assistant");
        var assistantId = _server.State.SeedAssistantMessage(session, "all done", completed: true);
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var page = await instance.GetMessagesAsync(session, new PageRequest(null, 50), TestContext.Current.CancellationToken);

        var message = page.Items.Single(item => item.MessageId == assistantId);
        message.Role.ShouldBe("assistant");
        message.CompletedAt.ShouldNotBeNull();
        message.Model.ShouldBe(new ModelRef("opencode", "fake-model"));
        message.Parts.Count.ShouldBe(1);
        message.Parts[0].Type.ShouldBe("text");
        message.Parts[0].Text.ShouldBe("all done");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Prompt_accepts_and_seeds_user_message()
    {
        var session = _server.State.SeedSession("prompting");
        _server.Scenario.PromptAcceptDelay = TimeSpan.FromMilliseconds(50);
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var accepted = await instance.PromptAsync(
            session,
            new PromptRequest("hello world", null, "build", "msg_client_1"),
            TestContext.Current.CancellationToken);

        accepted.MessageId.ShouldBe("msg_client_1");

        var messages = _server.State.GetMessages(session);
        var user = messages.Single(message => message.Info.Id == "msg_client_1");
        user.Info.Role.ShouldBe("user");
        user.Parts[0].Text.ShouldBe("hello world");

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Prompt_without_client_message_id_reports_empty_id()
    {
        var session = _server.State.SeedSession("prompting");
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var accepted = await instance.PromptAsync(
            session,
            new PromptRequest("no id", null, null, null),
            TestContext.Current.CancellationToken);

        accepted.MessageId.ShouldBeEmpty();

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Prompt_on_unknown_session_throws_not_found()
    {
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var exception = await Should.ThrowAsync<OpenCodeHttpException>(async () =>
            await instance.PromptAsync("ses_missing", new PromptRequest("x", null, null, null), TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Abort_returns_false_for_unknown_session()
    {
        var session = _server.State.SeedSession("aborting");
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var existing = await instance.AbortAsync(session, TestContext.Current.CancellationToken);
        existing.Aborted.ShouldBeTrue();

        var missing = await instance.AbortAsync("ses_missing", TestContext.Current.CancellationToken);
        missing.Aborted.ShouldBeFalse();

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Abort_ignored_once_keeps_session_busy_until_second_abort()
    {
        _server.Scenario.IgnoreAbortCount = 1;
        _server.Scenario.BusyDuration = TimeSpan.FromSeconds(10);
        _server.Scenario.ChunkDelay = TimeSpan.FromSeconds(10);
        var session = _server.State.SeedSession("abort-race");
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        await instance.PromptAsync(session, new PromptRequest("long turn", null, null, null), TestContext.Current.CancellationToken);

        var busy = await GetRunStateAsync(instance, session);
        busy.ShouldBe(AcRunState.Busy);

        var first = await instance.AbortAsync(session, TestContext.Current.CancellationToken);
        first.Aborted.ShouldBeTrue();
        (await GetRunStateAsync(instance, session)).ShouldBe(AcRunState.Busy);

        var second = await instance.AbortAsync(session, TestContext.Current.CancellationToken);
        second.Aborted.ShouldBeTrue();
        (await GetRunStateAsync(instance, session)).ShouldBe(AcRunState.Idle);

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task GetSession_returns_null_for_unknown_session()
    {
        var session = _server.State.SeedSession("find-me");
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var found = await instance.GetSessionAsync(session, TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found.Title.ShouldBe("find-me");

        var missing = await instance.GetSessionAsync("ses_missing", TestContext.Current.CancellationToken);
        missing.ShouldBeNull();

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task ListModels_flattens_providers()
    {
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);

        var models = await instance.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Count.ShouldBe(1);
        models[0].ShouldBe(new ModelOption("opencode", "fake-model", "Fake Model"));

        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Watch_streams_sse_events_and_permission_replies()
    {
        var session = _server.State.SeedSession("watching");
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);
        var received = new List<ProviderEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pump = PumpAsync(instance, received, cts.Token);

        await RuntimeTestHelpers.WaitUntilAsync(() => HasEvent(received, "server.connected"), TimeSpan.FromSeconds(5));

        var permissionId = _server.State.RequestPermission(session, "bash");
        await RuntimeTestHelpers.WaitUntilAsync(() => HasEvent(received, "permission.asked"), TimeSpan.FromSeconds(5));

        var asked = FirstEvent(received, "permission.asked")!;
        asked.Data!.Value.GetProperty("id").GetString().ShouldBe(permissionId);

        await instance.ReplyPermissionAsync(session, permissionId, PermissionReply.Once, TestContext.Current.CancellationToken);

        await RuntimeTestHelpers.WaitUntilAsync(
            () =>
            {
                var replied = FirstEvent(received, "permission.replied");
                return replied?.Data is { } data &&
                       data.TryGetProperty("response", out var response) &&
                       response.GetString() == "once";
            },
            TimeSpan.FromSeconds(5));

        await StopPumpAsync(cts, pump);
        await instance.DisposeAsync();
    }

    [Fact]
    public async Task Watch_reconnects_after_drop_and_signals_snapshot_required()
    {
        _server.Scenario.SseDropAfterEvents = 1;
        var instance = await DiscoverSingleAsync(CreateProvider(), TestContext.Current.CancellationToken);
        var received = new List<ProviderEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pump = PumpAsync(instance, received, cts.Token);

        await RuntimeTestHelpers.WaitUntilAsync(() => HasEvent(received, "server.connected"), TimeSpan.FromSeconds(5));
        await RuntimeTestHelpers.WaitUntilAsync(() => HasEvent(received, ProviderEventKinds.SnapshotRequired), TimeSpan.FromSeconds(10));
        await RuntimeTestHelpers.WaitUntilAsync(() => CountEvents(received, "server.connected") >= 2, TimeSpan.FromSeconds(10));

        await StopPumpAsync(cts, pump);
        await instance.DisposeAsync();
    }

    private async Task<AcRunState> GetRunStateAsync(IProviderInstance instance, string sessionId)
    {
        var sessions = await instance.ListSessionsAsync(
            new SessionQuery(instance.Info.Id, SessionListViews.Active, false, null),
            TestContext.Current.CancellationToken);
        return sessions.Single(summary => summary.Ref.SessionId == sessionId).RunState;
    }

    private static async Task PumpAsync(IProviderInstance instance, List<ProviderEvent> received, CancellationToken ct)
    {
        try
        {
            await foreach (var providerEvent in instance.WatchAsync(ct))
            {
                lock (received)
                {
                    received.Add(providerEvent);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task StopPumpAsync(CancellationTokenSource cts, Task pump)
    {
        await cts.CancelAsync();
        await pump;
    }

    private static bool HasEvent(List<ProviderEvent> received, string kind)
    {
        lock (received)
        {
            return received.Exists(providerEvent => providerEvent.Kind == kind);
        }
    }

    private static int CountEvents(List<ProviderEvent> received, string kind)
    {
        lock (received)
        {
            return received.Count(providerEvent => providerEvent.Kind == kind);
        }
    }

    private static ProviderEvent? FirstEvent(List<ProviderEvent> received, string kind)
    {
        lock (received)
        {
            return received.FirstOrDefault(providerEvent => providerEvent.Kind == kind);
        }
    }

    private OpenCodeProvider CreateProvider(string? instanceId = null, string? password = null) =>
        new(
            Machine,
            new OpenCodeProviderOptions("opencode", password ?? "test-pw", new[] { _server.BaseUrl }, instanceId),
            new ErrorClassifier(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

    private static async Task<IProviderInstance> DiscoverSingleAsync(OpenCodeProvider provider, CancellationToken ct)
    {
        var instances = await provider.DiscoverAsync(ct);
        instances.Count.ShouldBe(1);
        return instances[0];
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
