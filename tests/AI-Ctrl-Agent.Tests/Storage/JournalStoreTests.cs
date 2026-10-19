using System.Text.Json;
using AiCtrl.Client.Storage;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Serialization;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace AiCtrl.Tests.Storage;

public sealed class JournalStoreTests : IAsyncLifetime
{
    private static readonly SessionRef Session = new(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_1");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private ClientDb _db = null!;

    private JournalStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _db = new ClientDb(Path.Combine(_directory, "client.db"));
        await _db.InitializeAsync(TestContext.Current.CancellationToken);
        _store = new JournalStore(_db);
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
    public async Task Create_is_idempotent_for_same_command_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = Envelope("cmd_1", CommandTypes.SessionPrompt);

        var first = await _store.CreateIfAbsentAsync(envelope, ct);
        var second = await _store.CreateIfAbsentAsync(envelope, ct);

        first.Created.ShouldBeTrue();
        second.Created.ShouldBeFalse();
        second.Entry.CommandId.ShouldBe("cmd_1");
        second.Entry.Status.ShouldBe(CommandStatus.Acknowledged);
    }

    [Fact]
    public async Task Duplicate_command_returns_stored_terminal_result_without_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = Envelope("cmd_2", CommandTypes.SessionPrompt);
        await _store.CreateIfAbsentAsync(envelope, ct);
        await _store.UpdateAsync("cmd_2", CommandStatus.Running, null, null, null, DateTimeOffset.UnixEpoch, ct);
        var result = JsonSerializer.SerializeToElement(new { messageId = "msg_1" });
        await _store.UpdateAsync("cmd_2", CommandStatus.Succeeded, result, null, null, DateTimeOffset.UnixEpoch, ct);

        var duplicate = await _store.CreateIfAbsentAsync(envelope, ct);

        duplicate.Created.ShouldBeFalse();
        duplicate.Entry.Status.ShouldBe(CommandStatus.Succeeded);
        duplicate.Entry.ResultJson.ShouldBe(result.GetRawText());
    }

    [Fact]
    public async Task Recovering_running_side_effect_command_marks_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = Envelope("cmd_3", CommandTypes.SessionAbortAndPrompt);
        await _store.CreateIfAbsentAsync(envelope, ct);
        await _store.UpdateAsync("cmd_3", CommandStatus.Running, null, null, null, DateTimeOffset.UnixEpoch, ct);

        var recovered = await _store.RecoverInterruptedAsync(DateTimeOffset.UnixEpoch, ct);

        recovered.ShouldBe(1);
        var entry = await _store.GetAsync("cmd_3", ct);
        entry.ShouldNotBeNull();
        entry.Status.ShouldBe(CommandStatus.Unknown);
        entry.ErrorCode.ShouldBe("interrupted");
    }

    [Fact]
    public async Task Recovering_running_read_only_command_marks_acknowledged()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = Envelope("cmd_4", CommandTypes.InstanceRefresh);
        await _store.CreateIfAbsentAsync(envelope, ct);
        await _store.UpdateAsync("cmd_4", CommandStatus.Running, null, null, null, DateTimeOffset.UnixEpoch, ct);

        var recovered = await _store.RecoverInterruptedAsync(DateTimeOffset.UnixEpoch, ct);

        recovered.ShouldBe(1);
        var entry = await _store.GetAsync("cmd_4", ct);
        entry.ShouldNotBeNull();
        entry.Status.ShouldBe(CommandStatus.Acknowledged);
    }

    [Fact]
    public async Task Update_rejects_invalid_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = Envelope("cmd_5", CommandTypes.SessionPrompt);
        await _store.CreateIfAbsentAsync(envelope, ct);
        await _store.UpdateAsync("cmd_5", CommandStatus.Running, null, null, null, DateTimeOffset.UnixEpoch, ct);
        await _store.UpdateAsync("cmd_5", CommandStatus.Succeeded, null, null, null, DateTimeOffset.UnixEpoch, ct);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _store.UpdateAsync("cmd_5", CommandStatus.Running, null, null, null, DateTimeOffset.UnixEpoch, ct));
    }

    private static CommandEnvelope Envelope(string commandId, string type) =>
        new(
            commandId,
            new MachineId("m_test"),
            type,
            JsonSerializer.SerializeToElement(new SessionPromptPayload(Session, "hello", null, null), AcJsonContext.Default.SessionPromptPayload),
            "local",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            0);
}
