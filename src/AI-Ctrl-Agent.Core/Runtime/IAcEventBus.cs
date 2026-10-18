namespace AiCtrl.Core.Runtime;

public interface IAcEventBus
{
    ValueTask PublishAsync(AcEvent e, CancellationToken ct = default);

    IAsyncDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler) where T : AcEvent;
}
