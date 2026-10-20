using System.Collections.Concurrent;
using AiCtrl.Client.Providers.OpenCode.Backend;

namespace AiCtrl.Tests.Client;

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public ValueTask<string?> LoadAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_values.TryGetValue(key, out var value) ? value : null);
    }

    public ValueTask SaveAsync(string key, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _values[key] = value;
        return ValueTask.CompletedTask;
    }
}
