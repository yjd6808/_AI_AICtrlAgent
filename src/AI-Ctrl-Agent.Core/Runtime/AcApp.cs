using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Runtime;

public sealed class AcApp : IHostedService, IAsyncDisposable
{
    private const int MaxInitAttempts = 3;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _services;
    private readonly IEnumerable<IAcSystem> _systems;
    private readonly IAcEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly ILogger<AcApp> _logger;
    private readonly Action<int> _fatalExit;
    private readonly TimeSpan _initRetryBaseDelay;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IAcSystem> _started = new();
    private AcLoopSupervisor? _supervisor;

    public AcApp(
        IServiceProvider services,
        IEnumerable<IAcSystem> systems,
        IAcEventBus bus,
        TimeProvider time,
        ILoggerFactory logs,
        Action<int>? fatalExit = null,
        TimeSpan? initRetryBaseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logs);

        _services = services;
        _systems = systems;
        _bus = bus;
        _time = time;
        _logs = logs;
        _logger = logs.CreateLogger<AcApp>();
        _fatalExit = fatalExit ?? Environment.Exit;
        _initRetryBaseDelay = initRetryBaseDelay ?? TimeSpan.FromSeconds(1);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _supervisor = new AcLoopSupervisor(_time, _logs, _cts.Token);
        var ordered = TopologicalSort(_systems);
        var context = new AcSystemContext(_services, _bus, _time, _logs, _supervisor);

        foreach (var system in ordered)
        {
            await InitWithRetryAsync(system, context, cancellationToken).ConfigureAwait(false);
            await system.StartAsync(cancellationToken).ConfigureAwait(false);
            _started.Add(system);
            _logger.LogInformation("System '{System}' started", system.Name);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();

        for (var i = _started.Count - 1; i >= 0; i--)
        {
            var system = _started[i];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StopTimeout);

            try
            {
                await system.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System '{System}' stop failed", system.Name);
            }
        }

        _started.Clear();

        if (_supervisor is not null)
        {
            await _supervisor.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }

    private async ValueTask InitWithRetryAsync(IAcSystem system, AcSystemContext context, CancellationToken ct)
    {
        var delay = _initRetryBaseDelay;

        for (var attempt = 1; attempt <= MaxInitAttempts; attempt++)
        {
            try
            {
                await system.InitAsync(context, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxInitAttempts)
            {
                _logger.LogError(ex, "System '{System}' init failed; retrying ({Attempt}/{Max})", system.Name, attempt, MaxInitAttempts);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(delay.Ticks * 2);
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "System '{System}' init failed {Max} times in a row", system.Name, MaxInitAttempts);
                _fatalExit(3);
                throw new InvalidOperationException($"System '{system.Name}' failed to initialize.");
            }
        }
    }

    private static List<IAcSystem> TopologicalSort(IEnumerable<IAcSystem> systems)
    {
        var byName = new Dictionary<string, IAcSystem>(StringComparer.Ordinal);
        foreach (var system in systems)
        {
            if (!byName.TryAdd(system.Name, system))
            {
                throw new ApplicationException($"Duplicate system name '{system.Name}'.");
            }
        }

        var sorted = new List<IAcSystem>(byName.Count);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var system in byName.Values)
        {
            Visit(system, byName, state, sorted);
        }

        return sorted;
    }

    private static void Visit(IAcSystem system, Dictionary<string, IAcSystem> byName, Dictionary<string, int> state, List<IAcSystem> sorted)
    {
        if (state.TryGetValue(system.Name, out var current))
        {
            if (current == 1)
            {
                throw new ApplicationException($"Dependency cycle detected at system '{system.Name}'.");
            }

            return;
        }

        state[system.Name] = 1;

        foreach (var dependency in system.DependsOn)
        {
            if (!byName.TryGetValue(dependency, out var target))
            {
                throw new ApplicationException($"System '{system.Name}' depends on unknown system '{dependency}'.");
            }

            Visit(target, byName, state, sorted);
        }

        state[system.Name] = 2;
        sorted.Add(system);
    }
}
