using System.Net;
using System.Net.Sockets;
using AiCtrl.Client.Providers.OpenCode.Backend;
using AiCtrl.FakeOpenCode;
using AiCtrl.Tests.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Providers;

public sealed class OpenCodeBackendSupervisorTests
{
    private const string PasswordKey = "opencode.serve.password";

    [Fact]
    public async Task StartAsync_adopts_healthy_backend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = "adopt-pw", ct);
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "adopt-pw", ct);
        var factoryCalls = 0;

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(server.BaseUrl),
            _ =>
            {
                factoryCalls++;
                return new FakeBackendProcessController(_ => throw new InvalidOperationException("must not start a process"));
            },
            FixedProbe(server.BaseUrl, "adopt-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Healthy);
        state.Adopted.ShouldBeTrue();
        state.Version.ShouldBe("1.18.34");
        factoryCalls.ShouldBe(0);
    }

    [Fact]
    public async Task StartAsync_fails_with_port_in_use_when_password_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = "run-pw", ct);
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "other-pw", ct);

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(server.BaseUrl),
            _ => new FakeBackendProcessController(_ => throw new InvalidOperationException("must not start a process")),
            FixedProbe(server.BaseUrl, "other-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Failed);
        state.FailReason.ShouldBe("port_in_use");
        state.Adopted.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_fails_with_port_in_use_for_foreign_responder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOpenCodeServer.StartAsync(ct: ct);

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(server.BaseUrl),
            _ => new FakeBackendProcessController(_ => throw new InvalidOperationException("must not start a process")),
            new HttpBackendHealthProbe(
                new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(2) },
                "/not-an-opencode-route",
                "opencode",
                "whatever"),
            new InMemorySecretStore(),
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Failed);
        state.FailReason.ShouldBe("port_in_use");
    }

    [Fact]
    public async Task StartAsync_starts_managed_backend_when_unreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = FreeEndpoint();
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "managed-pw", ct);
        var receivedPasswords = new List<string>();
        var started = 0;

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(endpoint, startTimeout: TimeSpan.FromSeconds(10)),
            password =>
            {
                lock (receivedPasswords)
                {
                    receivedPasswords.Add(password);
                }

                return new FakeBackendProcessController(async token =>
                {
                    var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = password, token, endpoint);
                    return new FakeBackendProcess(100 + Interlocked.Increment(ref started), server);
                });
            },
            FixedProbe(endpoint, "managed-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Healthy);
        state.Adopted.ShouldBeFalse();
        state.ProcessId.ShouldBe(101);
        receivedPasswords.ShouldBe(new[] { "managed-pw" });
    }

    [Fact]
    public async Task StartAsync_generates_and_persists_password_when_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = FreeEndpoint();
        var secrets = new InMemorySecretStore();
        var probe = new MutablePasswordProbe(endpoint);
        var started = 0;

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(endpoint, startTimeout: TimeSpan.FromSeconds(10)),
            password =>
            {
                probe.Password = password;
                return new FakeBackendProcessController(async token =>
                {
                    var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = password, token, endpoint);
                    return new FakeBackendProcess(200 + Interlocked.Increment(ref started), server);
                });
            },
            probe,
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Healthy);
        supervisor.Password.ShouldNotBeNullOrEmpty();
        supervisor.Password!.Length.ShouldBe(64);
        (await secrets.LoadAsync(PasswordKey, ct)).ShouldBe(supervisor.Password);
    }

    [Fact]
    public async Task StartAsync_reports_start_failed_when_controller_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = FreeEndpoint();

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(endpoint),
            _ => new FakeBackendProcessController(_ => throw new InvalidOperationException("cannot spawn")),
            FixedProbe(endpoint, "pw"),
            new InMemorySecretStore(),
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Failed);
        state.FailReason.ShouldBe("start_failed");
    }

    [Fact]
    public async Task StartAsync_reports_health_timeout_and_kills_process()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = FreeEndpoint();
        FakeBackendProcess? process = null;

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(endpoint, startTimeout: TimeSpan.FromMilliseconds(300)),
            _ => new FakeBackendProcessController(_ =>
            {
                process = new FakeBackendProcess(300);
                return ValueTask.FromResult<IBackendProcess>(process);
            }),
            FixedProbe(endpoint, "pw"),
            new InMemorySecretStore(),
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);

        state.Status.ShouldBe(BackendStatus.Failed);
        state.FailReason.ShouldBe("health_timeout");
        process.ShouldNotBeNull();
        process.HasExited.ShouldBeTrue();
    }

    [Fact]
    public async Task WatchAsync_restarts_managed_backend_after_repeated_failures()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = FreeEndpoint();
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "watch-pw", ct);
        var started = 0;
        var servers = new List<FakeOpenCodeServer>();

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(endpoint, startTimeout: TimeSpan.FromSeconds(10)) with
            {
                WatchInterval = TimeSpan.FromMilliseconds(100),
                InitialRestartBackoff = TimeSpan.FromMilliseconds(100),
                MaxRestartBackoff = TimeSpan.FromMilliseconds(400),
            },
            password => new FakeBackendProcessController(async token =>
            {
                var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = password, token, endpoint);
                lock (servers)
                {
                    servers.Add(server);
                }

                return new FakeBackendProcess(400 + Interlocked.Increment(ref started), server);
            }),
            FixedProbe(endpoint, "watch-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        var state = await supervisor.StartAsync(ct);
        state.Status.ShouldBe(BackendStatus.Healthy);
        state.ProcessId.ShouldBe(401);

        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = Task.Run(() => supervisor.WatchAsync(watchCts.Token).AsTask(), CancellationToken.None);

        FakeOpenCodeServer firstServer;
        lock (servers)
        {
            firstServer = servers[0];
        }

        await firstServer.DisposeAsync();

        await RuntimeTestHelpers.WaitUntilAsync(
            () => started >= 2 && supervisor.State.ProcessId == 402,
            TimeSpan.FromSeconds(10));

        supervisor.State.Status.ShouldBe(BackendStatus.Healthy);
        supervisor.State.Adopted.ShouldBeFalse();

        await watchCts.CancelAsync();
        await watch;
    }

    [Fact]
    public async Task WatchAsync_marks_adopted_backend_unhealthy_without_restarting()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = "adopt-pw", ct);
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "adopt-pw", ct);
        var factoryCalls = 0;

        await using var supervisor = new OpenCodeBackendSupervisor(
            Options(server.BaseUrl) with
            {
                WatchInterval = TimeSpan.FromMilliseconds(100),
            },
            _ =>
            {
                factoryCalls++;
                return new FakeBackendProcessController(_ => throw new InvalidOperationException("must not start a process"));
            },
            FixedProbe(server.BaseUrl, "adopt-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        (await supervisor.StartAsync(ct)).Status.ShouldBe(BackendStatus.Healthy);

        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = Task.Run(() => supervisor.WatchAsync(watchCts.Token).AsTask(), CancellationToken.None);

        await server.DisposeAsync();

        await RuntimeTestHelpers.WaitUntilAsync(
            () => supervisor.State.Status == BackendStatus.Unhealthy,
            TimeSpan.FromSeconds(10));

        supervisor.State.Adopted.ShouldBeTrue();
        factoryCalls.ShouldBe(0);

        await watchCts.CancelAsync();
        await watch;
    }

    [Fact]
    public async Task DisposeAsync_kills_managed_process()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = FreeEndpoint();
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "managed-pw", ct);
        FakeBackendProcess? process = null;

        var supervisor = new OpenCodeBackendSupervisor(
            Options(endpoint, startTimeout: TimeSpan.FromSeconds(10)),
            _ => new FakeBackendProcessController(async token =>
            {
                var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = "managed-pw", token, endpoint);
                process = new FakeBackendProcess(500, server);
                return process;
            }),
            FixedProbe(endpoint, "managed-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        (await supervisor.StartAsync(ct)).Status.ShouldBe(BackendStatus.Healthy);

        await supervisor.DisposeAsync();

        process.ShouldNotBeNull();
        process.HasExited.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_keeps_adopted_backend_running()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOpenCodeServer.StartAsync(scenario => scenario.Password = "adopt-pw", ct);
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync(PasswordKey, "adopt-pw", ct);

        var supervisor = new OpenCodeBackendSupervisor(
            Options(server.BaseUrl),
            _ => new FakeBackendProcessController(_ => throw new InvalidOperationException("must not start a process")),
            FixedProbe(server.BaseUrl, "adopt-pw"),
            secrets,
            TimeProvider.System,
            NullLogger.Instance);

        (await supervisor.StartAsync(ct)).Adopted.ShouldBeTrue();

        await supervisor.DisposeAsync();

        var health = await FixedProbe(server.BaseUrl, "adopt-pw").ProbeAsync(ct);
        health.Status.ShouldBe(BackendHealthStatus.Healthy);
    }

    private static OpenCodeBackendSupervisorOptions Options(
        string endpoint,
        TimeSpan? startTimeout = null) =>
        new()
        {
            Endpoint = endpoint,
            HealthPollInterval = TimeSpan.FromMilliseconds(50),
            StartTimeout = startTimeout ?? TimeSpan.FromSeconds(5),
        };

    private static HttpBackendHealthProbe FixedProbe(string endpoint, string password) =>
        new(
            new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(2) },
            "/global/health",
            "opencode",
            password);

    private static string FreeEndpoint() => $"http://127.0.0.1:{GetFreePort()}";

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeBackendProcess : IBackendProcess
    {
        private readonly FakeOpenCodeServer? _server;
        private int _killed;

        public FakeBackendProcess(int id, FakeOpenCodeServer? server = null)
        {
            Id = id;
            _server = server;
        }

        public int Id { get; }

        public bool HasExited { get; private set; }

        public async Task KillAsync()
        {
            if (Interlocked.Exchange(ref _killed, 1) == 1)
            {
                return;
            }

            HasExited = true;
            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBackendProcessController : IBackendProcessController
    {
        private readonly Func<CancellationToken, ValueTask<IBackendProcess>> _start;

        public FakeBackendProcessController(Func<CancellationToken, ValueTask<IBackendProcess>> start)
        {
            _start = start;
        }

        public ValueTask<IBackendProcess> StartAsync(CancellationToken ct) => _start(ct);
    }

    private sealed class MutablePasswordProbe : IBackendHealthProbe
    {
        private readonly string _endpoint;

        public MutablePasswordProbe(string endpoint)
        {
            _endpoint = endpoint;
        }

        public string? Password { get; set; }

        public async ValueTask<BackendHealth> ProbeAsync(CancellationToken ct)
        {
            using var client = new HttpClient { BaseAddress = new Uri(_endpoint), Timeout = TimeSpan.FromSeconds(2) };
            var probe = new HttpBackendHealthProbe(client, "/global/health", "opencode", Password ?? string.Empty);
            return await probe.ProbeAsync(ct);
        }
    }
}
