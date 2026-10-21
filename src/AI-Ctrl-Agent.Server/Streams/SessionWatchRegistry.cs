using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;

namespace AiCtrl.Server.Streams;

public sealed class SessionWatchRegistry
{
    private readonly object _gate = new();

    private readonly Dictionary<WatchKey, WatchEntry> _entries = new();

    public bool WatchUi(string machineId, string sessionId, string connectionId)
    {
        var key = Key(machineId, sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
        {
            var existed = _entries.TryGetValue(key, out var entry);
            var active = existed && (entry!.UiConnections.Count > 0 || entry.RestUntil is not null);
            var entryValue = entry ?? new WatchEntry();
            if (!existed)
            {
                _entries[key] = entryValue;
            }

            var added = entryValue.UiConnections.Add(connectionId);
            return !active || added;
        }
    }

    public bool UnwatchUi(string machineId, string sessionId, string connectionId)
    {
        var key = Key(machineId, sessionId);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || !entry.UiConnections.Remove(connectionId))
            {
                return false;
            }

            if (entry.UiConnections.Count > 0 || entry.RestUntil is not null)
            {
                return false;
            }

            _entries.Remove(key);
            return true;
        }
    }

    public IReadOnlyList<string> RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in _entries.ToArray())
            {
                if (!pair.Value.UiConnections.Remove(connectionId))
                {
                    continue;
                }

                if (pair.Value.UiConnections.Count == 0 && pair.Value.RestUntil is null)
                {
                    _entries.Remove(pair.Key);
                }

                changed.Add(pair.Key.MachineId);
            }

            return changed.ToArray();
        }
    }

    public bool TouchRest(string machineId, string sessionId, DateTimeOffset now, DateTimeOffset until)
    {
        var key = Key(machineId, sessionId);
        lock (_gate)
        {
            var existed = _entries.TryGetValue(key, out var entry);
            var active = existed && entry!.IsActive(now);
            var entryValue = entry ?? new WatchEntry();
            if (!existed)
            {
                _entries[key] = entryValue;
            }

            entryValue.RestUntil = until;
            return !active;
        }
    }

    public IReadOnlyList<string> Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in _entries.ToArray())
            {
                if (pair.Value.UiConnections.Count > 0)
                {
                    if (pair.Value.RestUntil is { } until && until <= now)
                    {
                        pair.Value.RestUntil = null;
                    }

                    continue;
                }

                if (pair.Value.RestUntil is { } restUntil && restUntil > now)
                {
                    continue;
                }

                _entries.Remove(pair.Key);
                changed.Add(pair.Key.MachineId);
            }

            return changed.ToArray();
        }
    }

    public IReadOnlyList<SessionRef> GetWatched(string machineId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries
                .Where(pair => string.Equals(pair.Key.MachineId, machineId, StringComparison.Ordinal))
                .Where(pair => pair.Value.IsActive(now))
                .Select(pair => pair.Key.ToRef())
                .Distinct()
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetActiveMachines(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries
                .Where(pair => pair.Value.IsActive(now))
                .Select(pair => pair.Key.MachineId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static WatchKey Key(string machineId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }

        return new WatchKey(machineId, string.Empty, sessionId);
    }

    private sealed class WatchEntry
    {
        public HashSet<string> UiConnections { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? RestUntil { get; set; }

        public bool IsActive(DateTimeOffset now) =>
            UiConnections.Count > 0 || RestUntil is { } until && until > now;
    }

    private readonly record struct WatchKey(string MachineId, string InstanceId, string SessionId)
    {
        public SessionRef ToRef() => new(new MachineId(MachineId), new InstanceId(InstanceId), SessionId);
    }
}
