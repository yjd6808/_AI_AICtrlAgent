using System.Text.Json;
using AiCtrl.Client.Storage;
using AiCtrl.Core.Domain;

namespace AiCtrl.Client.Vpn;

public sealed class ConnectionHistory
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly object _gate = new();

    private readonly Dictionary<string, DateTimeOffset> _lastConnected =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _time;

    public ConnectionHistory(string? path, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        HistoryPath = string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(ClientPaths.DataDirectory, "vpn-history.json")
            : path;
        _time = time;
        Load();
    }

    public string HistoryPath { get; }

    public DateTimeOffset? GetLastConnected(string serverName)
    {
        lock (_gate)
        {
            return _lastConnected.TryGetValue(serverName, out var value) ? value : null;
        }
    }

    public void Record(string serverName, DateTimeOffset when)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        lock (_gate)
        {
            _lastConnected[serverName] = when;
            Prune(when);
            Save();
        }
    }

    public VpnRowState StateFor(string serverName, DateTimeOffset now) =>
        EvaluateState(GetLastConnected(serverName), now);

    public static VpnRowState EvaluateState(DateTimeOffset? lastConnected, DateTimeOffset now)
    {
        if (lastConnected is null)
        {
            return VpnRowState.Never;
        }

        return now - lastConnected.Value < RecentWindow ? VpnRowState.Recent : VpnRowState.Past;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return;
            }

            var raw = File.ReadAllText(HistoryPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(raw);
            if (parsed is null)
            {
                return;
            }

            foreach (var (key, value) in parsed)
            {
                _lastConnected[key] = value;
            }

            Prune(_time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        foreach (var key in _lastConnected
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lastConnected.Remove(key);
        }
    }

    private void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                _lastConnected,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
