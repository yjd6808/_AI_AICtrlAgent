using AiCtrl.Client.Storage;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace AiCtrl.Tests.Storage;

public sealed class ClientDbTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "client.db");

    public async ValueTask InitializeAsync()
    {
        var db = new ClientDb(DatabasePath);
        await db.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Machine_id_is_generated_once_and_persisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await new ClientDb(DatabasePath).GetOrCreateMachineIdAsync(ct);
        var second = await new ClientDb(DatabasePath).GetOrCreateMachineIdAsync(ct);

        first.Value.ShouldStartWith("m_");
        second.ShouldBe(first);
    }
}
