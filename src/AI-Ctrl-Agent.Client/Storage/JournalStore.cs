using System.Globalization;
using System.Text.Json;
using AiCtrl.Core.Commands;
using Microsoft.Data.Sqlite;

namespace AiCtrl.Client.Storage;

public sealed record JournalEntry(
    string CommandId,
    string Type,
    string PayloadJson,
    CommandStatus Status,
    string? ResultJson,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset ReceivedAt,
    DateTimeOffset UpdatedAt,
    bool Reported);

public sealed record JournalCreateResult(JournalEntry Entry, bool Created);

public sealed class JournalStore
{
    private const string SelectColumns =
        "command_id, type, payload_json, status, result_json, error_code, error_message, received_at, updated_at, reported";

    private static readonly HashSet<string> SideEffectTypes = new(StringComparer.Ordinal)
    {
        CommandTypes.SessionAbort,
        CommandTypes.SessionPrompt,
        CommandTypes.SessionAbortAndPrompt,
        CommandTypes.SessionCreate,
        CommandTypes.SessionPermissionReply,
        CommandTypes.MachineAbortAll,
        CommandTypes.OpenCodeBackendRestart,
        CommandTypes.VpnConnect,
        CommandTypes.VpnDisconnect,
        CommandTypes.FilesReceive,
        CommandTypes.FilesSend,
        CommandTypes.FilesMkdir,
        CommandTypes.FilesDelete,
        CommandTypes.FilesRename,
        CommandTypes.WorkspaceRegister,
        CommandTypes.PowerKeepAwake,
    };

    private readonly ClientDb _db;

    public JournalStore(ClientDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<JournalCreateResult> CreateIfAbsentAsync(CommandEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var timestamp = envelope.IssuedAt.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO journal
                (command_id, type, payload_json, status, result_json, error_code, error_message, received_at, updated_at, reported)
            VALUES
                ($id, $type, $payload, $status, NULL, NULL, NULL, $received, $updated, 0);
            """;
        command.Parameters.AddWithValue("$id", envelope.CommandId);
        command.Parameters.AddWithValue("$type", envelope.Type);
        command.Parameters.AddWithValue("$payload", envelope.Payload.GetRawText());
        command.Parameters.AddWithValue("$status", (int)CommandStatus.Acknowledged);
        command.Parameters.AddWithValue("$received", timestamp);
        command.Parameters.AddWithValue("$updated", timestamp);

        var created = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        var entry = await ReadAsync(connection, envelope.CommandId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Journal entry '{envelope.CommandId}' was not stored.");
        return new JournalCreateResult(entry, created);
    }

    public async Task<JournalEntry?> GetAsync(string commandId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await ReadAsync(connection, commandId, ct).ConfigureAwait(false);
    }

    public async Task<JournalEntry> UpdateAsync(
        string commandId,
        CommandStatus status,
        JsonElement? result,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var current = await GetAsync(commandId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Journal entry '{commandId}' does not exist.");
        CommandStateMachine.EnsureTransition(current.Status, status);

        var resultJson = result?.GetRawText();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE journal
            SET status = $status, result_json = $result, error_code = $code,
                error_message = $message, updated_at = $updated
            WHERE command_id = $id;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", commandId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return current with
        {
            Status = status,
            ResultJson = resultJson,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            UpdatedAt = updatedAt,
        };
    }

    public async Task<int> RecoverInterruptedAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var interrupted = new List<JournalEntry>();

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT {SelectColumns} FROM journal WHERE status IN ($running, $acknowledged);";
            select.Parameters.AddWithValue("$running", (int)CommandStatus.Running);
            select.Parameters.AddWithValue("$acknowledged", (int)CommandStatus.Acknowledged);

            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                interrupted.Add(ReadEntry(reader));
            }
        }

        var recovered = 0;
        foreach (var entry in interrupted)
        {
            var status = SideEffectTypes.Contains(entry.Type) ? CommandStatus.Unknown : CommandStatus.Acknowledged;
            if (status == entry.Status)
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE journal
                SET status = $status, updated_at = $updated,
                    error_code = CASE WHEN $status = $unknown THEN 'interrupted' ELSE error_code END,
                    error_message = CASE WHEN $status = $unknown THEN 'client restarted before completion' ELSE error_message END
                WHERE command_id = $id;
                """;
            update.Parameters.AddWithValue("$status", (int)status);
            update.Parameters.AddWithValue("$unknown", (int)CommandStatus.Unknown);
            update.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", entry.CommandId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            recovered++;
        }

        return recovered;
    }

    private static async Task<JournalEntry?> ReadAsync(SqliteConnection connection, string commandId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM journal WHERE command_id = $id;";
        command.Parameters.AddWithValue("$id", commandId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    private static JournalEntry ReadEntry(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            (CommandStatus)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt64(9) != 0);
}
