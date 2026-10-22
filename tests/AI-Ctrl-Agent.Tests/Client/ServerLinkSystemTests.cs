using System.Text.Json;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Link;
using AiCtrl.Client.Security;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Transfers;
using AiCtrl.Client.Workspace;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Runtime;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
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

    private StreamSystem _streams = null!;

    private AgentPushChannel _push = null!;

    private ProviderEventBroker _events = null!;

    private FakeProviderInstance _instance = null!;

    private FakeProviderInstanceRegistry _registry = null!;

    private AcLoopSupervisor? _loops;

    private ServiceProvider? _services;

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
        _instance = new FakeProviderInstance();
        _registry = new FakeProviderInstanceRegistry(machineId, new[] { _instance });
        var runner = new AbortAndPromptRunner(
            new AbortAndPromptOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(60)),
            TimeProvider.System,
            NullLogger<AbortAndPromptRunner>.Instance);
        var workspaces = new WorkspaceRegistry(new WorkspaceOptions([], []));
        var files = new FilesSystem(workspaces);
        var linkState = new TransferLinkState();
        var commands = new CommandSystem(
            journal,
            _actors,
            _registry,
            runner,
            files,
            TestTransferSystem.Create(_db, workspaces, linkState),
            TimeProvider.System,
            NullLogger<CommandSystem>.Instance);
        var state = new StateSystem(TimeProvider.System, new MonitorOptions(), NullLoggerFactory.Instance);
        var requests = new ClientRequestHandler(
            commands,
            journal,
            _registry,
            state,
            files,
            TimeProvider.System,
            NullLogger<ClientRequestHandler>.Instance);
        _events = new ProviderEventBroker(NullLogger<ProviderEventBroker>.Instance);
        _push = new AgentPushChannel(NullLogger<AgentPushChannel>.Instance);
        _streams = new StreamSystem(
            _registry,
            _events,
            _push,
            TimeProvider.System,
            NullLoggerFactory.Instance);
        _link = new ServerLinkSystem(
            _db,
            keys,
            _outbox,
            requests,
            _streams,
            _push,
            linkState,
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
        if (_loops is not null)
        {
            await _streams.StopAsync(TestContext.Current.CancellationToken);
            await _loops.DisposeAsync();
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _link.StopAsync(TestContext.Current.CancellationToken);
        await _actors.DisposeAsync();
        await _instance.DisposeAsync();
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
    public async Task Watched_session_streams_provider_text_to_the_server_ring()
    {
        var ct = TestContext.Current.CancellationToken;
        const string sessionId = "ses_stream";
        var logs = NullLoggerFactory.Instance;
        _loops = new AcLoopSupervisor(TimeProvider.System, logs);
        _services = new ServiceCollection().BuildServiceProvider();
        var ctx = new AcSystemContext(
            _services,
            new ChannelAcEventBus(logs),
            TimeProvider.System,
            logs,
            _loops);
        await _streams.InitAsync(ctx, ct);
        await _streams.StartAsync(ct);

        await _link.StartAsync(ct);
        await WaitUntilAsync(() => Task.FromResult(_link.ConnectedCount >= 1), TimeSpan.FromSeconds(10), ct);

        var machineId = await _db.GetMachineIdAsync(ct);
        var agentConnection = _host.Services.GetRequiredService<ConnectionRegistry>().GetConnection(machineId!.Value.Value);
        agentConnection.ShouldNotBeNullOrEmpty();
        var watches = _host.Services.GetRequiredService<SessionWatchCoordinator>();
        await watches.WatchUiAsync(machineId!.Value.Value, sessionId, agentConnection!, ct);

        var streams = _host.Services.GetRequiredService<SessionStreamHub>();
        StreamReadResult? read = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            _events.Publish(
                _instance,
                new ProviderEvent(
                    "message.updated",
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["sessionID"] = sessionId,
                        ["info"] = new Dictionary<string, object?>
                        {
                            ["id"] = "msg_stream",
                            ["role"] = "assistant",
                        },
                    })));
            _events.Publish(
                _instance,
                new ProviderEvent(
                    "message.part.updated",
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["sessionID"] = sessionId,
                        ["part"] = new Dictionary<string, object?>
                        {
                            ["id"] = "prt_stream",
                            ["messageID"] = "msg_stream",
                            ["type"] = "text",
                            ["text"] = "hello",
                        },
                    })));

            read = await streams.ReadAsync(machineId.Value.Value, sessionId, null, TimeSpan.Zero, ct);
            if (read.Chunks.Count > 0)
            {
                break;
            }

            await Task.Delay(100, ct);
        }

        read.ShouldNotBeNull();
        var chunk = read.Chunks[0];
        chunk.Op.ShouldBe(StreamOp.Replace);
        chunk.Text.ShouldBe("hello");
        chunk.Seq.ShouldBe(1);
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
