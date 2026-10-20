using AiCtrl.Client.Storage;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace AiCtrl.Tests.Storage;

public sealed class OutboxStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private ClientDb _db = null!;

    private OutboxStore _outbox = null!;

    public async ValueTask InitializeAsync()
    {
        _db = new ClientDb(Path.Combine(_directory, "client.db"));
        await _db.InitializeAsync(TestContext.Current.CancellationToken);
        _outbox = new OutboxStore(_db, TimeProvider.System);
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
    public async Task Append_and_ack_advance_sequences()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _outbox.AppendAsync("event", "{\"a\":1}", ct);
        var second = await _outbox.AppendAsync("event", "{\"a\":2}", ct);

        first.Seq.ShouldBe(1);
        second.Seq.ShouldBe(2);
        (await _outbox.CountAsync(ct)).ShouldBe(2);
        (await _outbox.GetLastAckedAsync(ct)).ShouldBe(0);
        (await _outbox.GetUnackedAsync(10, ct)).Select(x => x.Seq).ShouldBe([1, 2]);

        await _outbox.AckAsync(1, ct);
        (await _outbox.CountAsync(ct)).ShouldBe(1);
        (await _outbox.GetLastAckedAsync(ct)).ShouldBe(1);
        var remaining = await _outbox.GetUnackedAsync(10, ct);
        remaining.Single().Seq.ShouldBe(2);
    }

    [Fact]
    public async Task Ack_is_monotonic_and_sequence_survives_reopen()
    {
        var ct = TestContext.Current.CancellationToken;
        await _outbox.AppendAsync("event", "{}", ct);
        await _outbox.AckAsync(1, ct);
        await _outbox.AckAsync(0, ct);
        (await _outbox.GetLastAckedAsync(ct)).ShouldBe(1);

        var reopened = new OutboxStore(new ClientDb(_db.DatabasePath), TimeProvider.System);
        var next = await reopened.AppendAsync("event", "{}", ct);
        next.Seq.ShouldBe(2);
    }
}
