namespace AiCtrl.Core.Runtime;

public interface IAcSystem
{
    string Name { get; }

    IReadOnlyList<string> DependsOn { get; }

    ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct);

    ValueTask StartAsync(CancellationToken ct);

    ValueTask StopAsync(CancellationToken ct);
}
