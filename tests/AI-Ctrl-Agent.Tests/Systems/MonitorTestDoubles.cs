using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiCtrl.Tests.Systems;

internal sealed class FakeProviderInstance : IProviderInstance
{
    private readonly Channel<ProviderEvent> _channel = Channel.CreateUnbounded<ProviderEvent>();

    public FakeProviderInstance(string instanceId = "opencode:test")
    {
        Info = new InstanceInfo(
            new InstanceId(instanceId),
            ProviderKind.OpenCode,
            "http://127.0.0.1:4096",
            "1.18.34",
            InstanceMode.SharedStore,
            Healthy: true,
            Managed: false,
            CapabilitySet.ListSessions | CapabilitySet.ReadMessages | CapabilitySet.LiveEvents);
    }

    public InstanceInfo Info { get; }

    public CapabilitySet Capabilities => Info.Capabilities;

    public List<SessionSummary> Sessions { get; } = new();

    public List<MessageView> Messages { get; } = new();

    public List<PromptRequest> Prompts { get; } = new();

    public List<(string SessionId, string PermissionId, PermissionReply Reply)> PermissionReplies { get; } = new();

    public void Emit(string kind, Dictionary<string, object?>? properties) =>
        _channel.Writer.TryWrite(new ProviderEvent(
            kind,
            properties is null ? null : JsonSerializer.SerializeToElement(properties)));

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionQuery q, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<SessionSummary>>(Sessions.ToList());

    public ValueTask<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken ct) =>
        ValueTask.FromResult(Sessions.FirstOrDefault(summary => summary.Ref.SessionId == sessionId));

    public ValueTask<Page<MessageView>> GetMessagesAsync(string sessionId, PageRequest page, CancellationToken ct) =>
        ValueTask.FromResult(new Page<MessageView>(Messages.ToList(), null, false));

    public ValueTask<IReadOnlyList<ModelOption>> ListModelsAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ModelOption>>(Array.Empty<ModelOption>());

    public ValueTask<AbortResult> AbortAsync(string sessionId, CancellationToken ct) =>
        ValueTask.FromResult(new AbortResult(true, null));

    public ValueTask<PromptAccepted> PromptAsync(string sessionId, PromptRequest req, CancellationToken ct)
    {
        Prompts.Add(req);
        return ValueTask.FromResult(new PromptAccepted(req.ClientMessageId ?? string.Empty, DateTimeOffset.UnixEpoch));
    }

    public ValueTask ReplyPermissionAsync(string sessionId, string permissionId, PermissionReply reply, CancellationToken ct)
    {
        PermissionReplies.Add((sessionId, permissionId, reply));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ProviderEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var providerEvent in _channel.Reader.ReadAllAsync(ct))
        {
            yield return providerEvent;
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeProviderInstanceRegistry : IProviderInstanceRegistry
{
    public FakeProviderInstanceRegistry(MachineId machineId, IReadOnlyList<IProviderInstance> instances)
    {
        MachineId = machineId;
        Instances = instances;
    }

    public MachineId MachineId { get; }

    public IReadOnlyList<IProviderInstance> Instances { get; }
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public bool HasMessage(string category, string fragment) =>
        Entries.Any(entry => entry.Category == category && entry.Message.Contains(fragment, StringComparison.Ordinal));

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string category, LogLevel level, string message)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(category, level, message));
        }
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerFactory _owner;
        private readonly string _category;

        public CapturingLogger(CapturingLoggerFactory owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _owner.Add(_category, logLevel, formatter(state, exception));
    }
}

internal sealed class EventCollector : IAsyncDisposable
{
    private readonly List<AcEvent> _events = new();
    private readonly IAsyncDisposable _subscription;

    public EventCollector(ChannelAcEventBus bus)
    {
        _subscription = bus.Subscribe<AcEvent>((e, ct) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }

            return ValueTask.CompletedTask;
        });
    }

    public IReadOnlyList<AcEvent> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToList();
            }
        }
    }

    public T? First<T>() where T : AcEvent
    {
        lock (_events)
        {
            return _events.OfType<T>().FirstOrDefault();
        }
    }

    public ValueTask DisposeAsync() => _subscription.DisposeAsync();
}

internal sealed class MonitorHarness : IAsyncDisposable
{
    private readonly AcLoopSupervisor _loops;
    private readonly MonitorSystem _monitor;

    private MonitorHarness(
        FakeProviderInstance instance,
        ChannelAcEventBus bus,
        CapturingLoggerFactory logs,
        FakeTimeProvider time,
        AcLoopSupervisor loops,
        MonitorSystem monitor)
    {
        Instance = instance;
        Bus = bus;
        Logs = logs;
        Time = time;
        _loops = loops;
        _monitor = monitor;
    }

    public FakeProviderInstance Instance { get; }

    public ChannelAcEventBus Bus { get; }

    public CapturingLoggerFactory Logs { get; }

    public FakeTimeProvider Time { get; }

    public static async Task<MonitorHarness> StartAsync(
        CancellationToken ct,
        MonitorOptions? options = null,
        ProviderEventBroker? events = null)
    {
        var time = new FakeTimeProvider();
        var logs = new CapturingLoggerFactory();
        var bus = new ChannelAcEventBus(logs);
        var loops = new AcLoopSupervisor(time, logs);
        var instance = new FakeProviderInstance();
        var registry = new FakeProviderInstanceRegistry(new MachineId("m_test"), new[] { instance });
        var monitor = new MonitorSystem(registry, options ?? new MonitorOptions(), time, logs, events: events);
        var ctx = new AcSystemContext(new ServiceCollection().BuildServiceProvider(), bus, time, logs, loops);

        await monitor.InitAsync(ctx, ct);
        await monitor.StartAsync(ct);
        return new MonitorHarness(instance, bus, logs, time, loops, monitor);
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.StopAsync(CancellationToken.None);
        await _loops.DisposeAsync();
        await Instance.DisposeAsync();
    }
}
