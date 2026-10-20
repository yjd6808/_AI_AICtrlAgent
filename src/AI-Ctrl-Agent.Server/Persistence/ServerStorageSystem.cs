using AiCtrl.Core.Runtime;

namespace AiCtrl.Server.Persistence;

public sealed class ServerStorageSystem : IAcSystem
{
    private readonly ServerDb _db;

    public ServerStorageSystem(ServerDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public string Name => "server-storage";

    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask StartAsync(CancellationToken ct)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
    }

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
