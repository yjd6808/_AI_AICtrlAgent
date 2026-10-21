using System.Collections.Concurrent;
using AiCtrl.Core.Domain;
using AiCtrl.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Server.Streams;

public sealed class SessionWatchCoordinator : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly SessionWatchRegistry _registry;

    private readonly ConnectionRegistry _connections;

    private readonly IHubContext<AgentHub> _hub;

    private readonly StreamOptions _options;

    private readonly TimeProvider _time;

    private readonly ILogger<SessionWatchCoordinator> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public SessionWatchCoordinator(
        SessionWatchRegistry registry,
        ConnectionRegistry connections,
        IHubContext<AgentHub> hub,
        StreamOptions options,
        TimeProvider time,
        ILogger<SessionWatchCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _connections = connections;
        _hub = hub;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task WatchUiAsync(string machineId, string sessionId, string connectionId, CancellationToken ct)
    {
        if (_registry.WatchUi(machineId, sessionId, connectionId))
        {
            await NotifyAsync(machineId, ct).ConfigureAwait(false);
        }
    }

    public async Task UnwatchUiAsync(string machineId, string sessionId, string connectionId, CancellationToken ct)
    {
        if (_registry.UnwatchUi(machineId, sessionId, connectionId))
        {
            await NotifyAsync(machineId, ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveConnectionAsync(string connectionId, CancellationToken ct)
    {
        foreach (var machineId in _registry.RemoveConnection(connectionId))
        {
            await NotifyAsync(machineId, ct).ConfigureAwait(false);
        }
    }

    public async Task TouchRestAsync(string machineId, string sessionId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var until = now.AddSeconds(_options.EffectiveLeaseSeconds);
        if (_registry.TouchRest(machineId, sessionId, now, until))
        {
            await NotifyAsync(machineId, ct).ConfigureAwait(false);
        }
    }

    public Task SendToConnectionAsync(string machineId, string connectionId, CancellationToken ct) =>
        SendCoreAsync(machineId, connectionId, ct);

    public Task NotifyAsync(string machineId, CancellationToken ct) =>
        SendCoreAsync(machineId, connectionId: null, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextHeartbeat = _time.GetUtcNow() + HeartbeatInterval;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, _time, stoppingToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                foreach (var machineId in _registry.Expire(now))
                {
                    await NotifyAsync(machineId, stoppingToken).ConfigureAwait(false);
                }

                if (now < nextHeartbeat)
                {
                    continue;
                }

                nextHeartbeat = now + HeartbeatInterval;
                foreach (var machineId in _registry.GetActiveMachines(now))
                {
                    await NotifyAsync(machineId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendCoreAsync(string machineId, string? connectionId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(machineId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = connectionId ?? _connections.GetConnection(machineId);
            if (connection is null)
            {
                return;
            }

            var sessions = _registry.GetWatched(machineId, _time.GetUtcNow());
            await _hub.Clients
                .Client(connection)
                .SendAsync("SetWatchedSessions", sessions.ToArray(), _options.EffectiveLeaseSeconds, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to send watched sessions to machine {MachineId}", machineId);
        }
        finally
        {
            gate.Release();
        }
    }
}
