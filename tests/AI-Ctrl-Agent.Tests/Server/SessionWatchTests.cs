using System.Net;
using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class SessionWatchTests
{
    [Fact]
    public void Rest_watch_expires_after_lease()
    {
        var registry = new SessionWatchRegistry();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        registry.TouchRest("machine-1", "session-1", now, now.AddSeconds(60)).ShouldBeTrue();
        registry.GetWatched("machine-1", now).ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
        registry.Expire(now.AddSeconds(59)).ShouldBeEmpty();
        registry.GetWatched("machine-1", now.AddSeconds(59)).Count.ShouldBe(1);
        registry.Expire(now.AddSeconds(61)).ShouldBe(["machine-1"]);
        registry.GetWatched("machine-1", now.AddSeconds(61)).ShouldBeEmpty();
    }

    [Fact]
    public void Touch_after_rest_lease_expiry_reactivates_the_watch()
    {
        var registry = new SessionWatchRegistry();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        registry.TouchRest("machine-1", "session-1", now, now.AddSeconds(60)).ShouldBeTrue();
        registry.TouchRest("machine-1", "session-1", now.AddSeconds(61), now.AddSeconds(121)).ShouldBeTrue();
        registry.GetWatched("machine-1", now.AddSeconds(61)).Count.ShouldBe(1);
    }

    [Fact]
    public void Ui_watch_keeps_session_after_rest_lease_expires()
    {
        var registry = new SessionWatchRegistry();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        registry.WatchUi("machine-1", "session-1", "ui-1").ShouldBeTrue();
        registry.TouchRest("machine-1", "session-1", now, now.AddSeconds(60));
        registry.Expire(now.AddSeconds(61)).ShouldBeEmpty();
        registry.GetWatched("machine-1", now.AddSeconds(61)).Count.ShouldBe(1);

        registry.UnwatchUi("machine-1", "session-1", "ui-1").ShouldBeTrue();
        registry.GetWatched("machine-1", now.AddSeconds(61)).ShouldBeEmpty();
    }

    [Fact]
    public void Removing_ui_connection_drops_its_watches()
    {
        var registry = new SessionWatchRegistry();

        registry.WatchUi("machine-1", "session-1", "ui-1");
        registry.WatchUi("machine-1", "session-2", "ui-1");
        registry.WatchUi("machine-1", "session-1", "ui-2");

        registry.RemoveConnection("ui-1").ShouldBe(["machine-1"]);
        var watched = registry.GetWatched("machine-1", DateTimeOffset.MaxValue);
        watched.ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
    }

    [Fact]
    public async Task Agent_receives_watch_set_on_hello_and_ui_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = TestServerHost.CreateDirectory();
        await using var host = await TestServerHost.StartAsync(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var machineId = await EnrollAsync(host, key, ct);
        var token = await TokenAsync(host, key, machineId, ct);

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var browser = new HttpClient(handler) { BaseAddress = new Uri(host.BaseUrl) };
        using var login = await browser.PostAsync("/test-login", null, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var ui = new HubConnectionBuilder()
            .WithUrl(host.BaseUrl + "/hubs/ui", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => handler;
                options.Cookies = cookies;
            })
            .Build();
        var snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.On<FleetSnapshotDto>("FleetSnapshot", _ => snapshot.TrySetResult());
        await ui.StartAsync(ct);
        await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await ui.InvokeAsync("WatchSession", machineId, "session-1", ct);

        var first = new TaskCompletionSource<(SessionRef[] Sessions, int Lease)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<(SessionRef[] Sessions, int Lease)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = new HubConnectionBuilder()
            .WithUrl(host.BaseUrl + "/hubs/agent", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var watchCount = 0;
        agent.On<SessionRef[], int>("SetWatchedSessions", (sessions, lease) =>
        {
            if (Interlocked.Increment(ref watchCount) == 1)
            {
                first.TrySetResult((sessions, lease));
            }
            else
            {
                second.TrySetResult((sessions, lease));
            }
        });
        await agent.StartAsync(ct);
        await agent.InvokeAsync<HelloAck>(
            "Hello",
            new HelloRequest(machineId, "test", "boot-watch", 0, Array.Empty<string>()),
            ct);

        var initial = await first.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        initial.Sessions.ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
        initial.Sessions[0].Machine.ShouldBe(machineId);
        initial.Lease.ShouldBe(60);

        await ui.InvokeAsync("UnwatchSession", machineId, "session-1", ct);
        var updated = await second.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        updated.Sessions.ShouldBeEmpty();
    }

    private static async Task<MachineId> EnrollAsync(TestServerHost host, ECDsa key, CancellationToken ct)
    {
        var service = host.Services.GetRequiredService<EnrollmentService>();
        var code = await service.CreateCodeAsync(ct);
        var result = await service.EnrollAsync(
            new EnrollmentRequest(
                code.Code,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                "test-host",
                "test-os",
                "1.0.0"),
            "test",
            ct);
        result.Success.ShouldBeTrue($"enroll failed: {result.ErrorCode}");
        return new MachineId(result.MachineId!);
    }

    private static async Task<string> TokenAsync(
        TestServerHost host,
        ECDsa key,
        MachineId machineId,
        CancellationToken ct)
    {
        var auth = host.Services.GetRequiredService<AgentAuthService>();
        var challenge = await auth.ChallengeAsync(machineId, ct);
        var payload = Encoding.UTF8.GetBytes($"aictrl-agent-v1|{machineId.Value}|{challenge.Value}");
        var signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));
        var token = await auth.IssueTokenAsync(
            new AgentTokenRequest(machineId, challenge.Value!, signature),
            ct);
        token.Success.ShouldBeTrue();
        return token.Value!;
    }
}
