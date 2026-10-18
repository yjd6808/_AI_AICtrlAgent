namespace AiCtrl.Client.Providers.OpenCode.Backend;

public interface IBackendProcess : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Task KillAsync();
}
