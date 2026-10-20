using System.Collections.Concurrent;

namespace AiCtrl.Server.Hubs;

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, string> _byMachine = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, string> _byConnection = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    public ConnectionRegistry(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public string? Register(string machineId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        _byConnection[connectionId] = machineId;
        return _byMachine.AddOrUpdate(machineId, connectionId, (_, previous) =>
        {
            _byConnection.TryRemove(new KeyValuePair<string, string>(previous, machineId));
            return connectionId;
        });
    }

    public string? Remove(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var machineId))
        {
            return null;
        }

        _byMachine.TryRemove(new KeyValuePair<string, string>(machineId, connectionId));
        return machineId;
    }

    public string? GetConnection(string machineId) => _byMachine.GetValueOrDefault(machineId);

    public bool IsCurrent(string machineId, string connectionId) =>
        _byMachine.TryGetValue(machineId, out var current) && string.Equals(current, connectionId, StringComparison.Ordinal);

    public async Task<string?> WaitForConnectionAsync(
        string machineId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var connection = GetConnection(machineId);
        if (connection is not null || timeout <= TimeSpan.Zero)
        {
            return connection;
        }

        var deadline = _time.GetUtcNow() + timeout;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, ct).ConfigureAwait(false);
            connection = GetConnection(machineId);
            if (connection is not null)
            {
                return connection;
            }
        }

        return GetConnection(machineId);
    }
}
