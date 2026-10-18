namespace AiCtrl.Client.Providers.OpenCode.Backend;

public interface ISecretStore
{
    ValueTask<string?> LoadAsync(string key, CancellationToken ct);

    ValueTask SaveAsync(string key, string value, CancellationToken ct);
}
