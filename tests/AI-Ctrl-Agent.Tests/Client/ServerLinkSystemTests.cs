using AiCtrl.Client.Commands;
using AiCtrl.Client.Link;
using AiCtrl.Client.Security;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Security;
using AiCtrl.Tests.Server;
using AiCtrl.Tests.Systems;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class ServerLinkSystemTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly string _clientDirectory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private readonly InMemoryServerSecretStore _secrets = new();

    private TestServerHost _host = null!;

    private ServerLinkSystem _link = null!;

    private ClientDb _db = null!;

    private OutboxStore _outbox = null!;

    private SessionActorPool _actors = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(_directory, _secrets);
        _db = new ClientDb(Path.Combine(_clientDirectory, "client.db"));
        await _db.InitializeAsync(TestContext.Current.CancellationToken);
        _outbox = new OutboxStore(_db, TimeProvider.System);
        var secrets = new InMemorySecretStore();
        var keys = new DeviceKeyStore(secrets);
        var ct = TestContext.Current.CancellationToken;
        var key = await keys.GetOrCreateAsync(ct);
        var enrollment = _host.Services.GetRequiredService<EnrollmentService>();
        var code = await enrollment.CreateCodeAsync(ct);
        var enrolled = await enrollment.EnrollAsync(
            new EnrollmentRequest(code.Code, key.PublicKey, "test-host", "test-os", "1.0.0"),
            "test",
            ct);
        enrolled.Success.ShouldBeTrue($"enroll failed: {enrolled.ErrorCode}");
        var machineId = new MachineId(enrolled.MachineId!);
        await _db.SetMachineIdAsync(machineId, enrolled: true, ct);
        await _db.SetValueAsync("server.url", _host.BaseUrl, ct);
        var journal = new JournalStore(_db);
        _actors = new SessionActorPool(NullLoggerFactory.Instance);
        var registry = new FakeProviderInstanceRegistry(machineId, Array.Empty<IProviderInstance>());
        var runner = new AbortAndPromptRunner(
            new AbortAndPromptOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(60)),
            TimeProvider.System,
            NullLogger<AbortAndPromptRunner>.Instance);
        var commands = new CommandSystem(
            journal,
            _actors,
            registry,
            runner,
            TimeProvider.System,
            NullLogger<CommandSystem>.Instance);
        var state = new StateSystem(TimeProvider.System, new MonitorOptions(), NullLoggerFactory.Instance);
        var requests = new ClientRequestHandler(
            commands,
            journal,
            registry,
            state,
            TimeProvider.System,
            NullLogger<ClientRequestHandler>.Instance);
        _link = new ServerLinkSystem(
            _db,
            keys,
            _outbox,
            requests,
            new ServerLinkOptions
            {
                BaseUrl = _host.BaseUrl,
                InitialBackoff = TimeSpan.FromMilliseconds(100),
                MaxBackoff = TimeSpan.FromMilliseconds(200),
            },
            new NetworkChangeSignal(),
            TimeProvider.System,
            NullLogger<ServerLinkSystem>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _link.StopAsync(TestContext.Current.CancellationToken);
        await _actors.DisposeAsync();
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _directory, _clientDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Stop_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _link.StopAsync(ct);
        await _link.StopAsync(ct);
    }

    [Fact]
    public async Task Link_flushes_outbox_and_reconnects_after_server_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await _outbox.AppendAsync("test", "{\"value\":1}", ct);
        await _link.StartAsync(ct);
        await WaitUntilAsync(() => Task.FromResult(_link.ConnectedCount >= 1), TimeSpan.FromSeconds(10), ct);

        await WaitUntilAsync(
            async () =>
            {
                var machineId = await _db.GetMachineIdAsync(ct);
                var db = _host.Services.GetRequiredService<ServerDb>();
                var machine = await db.GetMachineAsync(machineId!.Value.Value, ct);
                return machine?.LastAppliedEventSeq == 1;
            },
            TimeSpan.FromSeconds(10),
            ct);
        await WaitUntilAsync(async () => await _outbox.CountAsync(ct) == 0, TimeSpan.FromSeconds(10), ct);

        var port = _host.Port;
        await _host.DisposeAsync();
        _host = await TestServerHost.StartAsync(_directory, _secrets, port: port);

        try
        {
            await WaitUntilAsync(() => Task.FromResult(_link.ConnectedCount >= 2), TimeSpan.FromSeconds(15), ct);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"reconnect failed; attempts={_link.Attempts}; lastError={_link.LastError}", ex);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before the timeout.");
            }

            await Task.Delay(50, ct);
        }
    }
}
