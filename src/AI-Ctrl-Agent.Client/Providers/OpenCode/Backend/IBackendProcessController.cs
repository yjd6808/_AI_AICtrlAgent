namespace AiCtrl.Client.Providers.OpenCode.Backend;

public interface IBackendProcessController
{
    ValueTask<IBackendProcess> StartAsync(CancellationToken ct);
}
