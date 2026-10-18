using System.Collections.Concurrent;

namespace AiCtrl.Client.Providers.OpenCode.Backend;

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public ValueTask<string?> LoadAsync(string key, CancellationToken ct) =>
        ValueTask.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public ValueTask SaveAsync(string key, string value, CancellationToken ct)
    {
        _values[key] = value;
        return ValueTask.CompletedTask;
    }
}
