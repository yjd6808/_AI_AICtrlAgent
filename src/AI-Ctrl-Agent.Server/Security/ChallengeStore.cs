using System.Security.Cryptography;

namespace AiCtrl.Server.Security;

public sealed class ChallengeStore
{
    private readonly Dictionary<string, ChallengeEntry> _entries = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    public ChallengeStore(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public string Create(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (_entries)
        {
            RemoveExpired();
            _entries[Key(machineId, nonce)] = new ChallengeEntry(_time.GetUtcNow().Add(AiCtrl.Core.Protocol.AgentTokenDefaults.ChallengeLifetime));
        }

        return nonce;
    }

    public bool TryConsume(string machineId, string nonce)
    {
        lock (_entries)
        {
            var key = Key(machineId, nonce);
            if (!_entries.Remove(key, out var entry))
            {
                return false;
            }

            return entry.ExpiresAt > _time.GetUtcNow();
        }
    }

    private void RemoveExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(key);
        }
    }

    private static string Key(string machineId, string nonce) => machineId + "|" + nonce;

    private sealed record ChallengeEntry(DateTimeOffset ExpiresAt);
}
