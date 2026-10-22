using System.Globalization;
using AiCtrl.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AiCtrl.Client.Storage;

public sealed class ClientDb
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS journal (
            command_id TEXT NOT NULL PRIMARY KEY,
            type TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            status INTEGER NOT NULL,
            result_json TEXT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            received_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            reported INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_journal_status ON journal(status);
        CREATE TABLE IF NOT EXISTS outbox (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            type TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            occurred_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS kv (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS instance_session_seen (
            instance_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            last_busy_at TEXT NOT NULL,
            PRIMARY KEY (instance_id, session_id)
        );
        CREATE TABLE IF NOT EXISTS transfers (
            transfer_id TEXT NOT NULL PRIMARY KEY,
            direction TEXT NOT NULL,
            workspace_id TEXT NOT NULL,
            rel_path TEXT NOT NULL,
            size INTEGER NOT NULL,
            sha256 TEXT NULL,
            received_json TEXT NOT NULL DEFAULT '[]',
            status TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_transfers_status ON transfers(status);
        """;

    private readonly string _connectionString;

    private readonly TimeProvider _time;

    public ClientDb(string databasePath, TimeProvider? time = null)
    {
        DatabasePath = databasePath;
        _time = time ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
    }

    public async Task<MachineId> GetOrCreateMachineIdAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var existing = await ReadMachineIdAsync(connection, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Value;
        }

        var generated = new MachineId("m_" + Guid.NewGuid().ToString("N"));
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO kv (key, value, updated_at) VALUES ('machine_id', $value, $now);";
            insert.Parameters.AddWithValue("$value", generated.Value);
            insert.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return await ReadMachineIdAsync(connection, ct).ConfigureAwait(false) ?? generated;
    }

    public async Task<MachineId?> GetMachineIdAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await ReadMachineIdAsync(connection, ct).ConfigureAwait(false);
    }

    public async Task SetMachineIdAsync(MachineId machineId, bool enrolled, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await SetValueAsync(connection, transaction, "machine_id", machineId.Value, now, ct).ConfigureAwait(false);
        await SetValueAsync(connection, transaction, "enrolled", enrolled ? "true" : "false", now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsEnrolledAsync(CancellationToken ct) =>
        string.Equals(await GetValueAsync("enrolled", ct).ConfigureAwait(false), "true", StringComparison.Ordinal);

    public async Task<string?> GetValueAsync(string key, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM kv WHERE key = $key;";
        select.Parameters.AddWithValue("$key", key);
        return await select.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task SetValueAsync(string key, string value, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO kv (key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertTransferAsync(ClientTransferRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfers (transfer_id, direction, workspace_id, rel_path, size, sha256, received_json, status, updated_at)
            VALUES ($id, $direction, $workspace, $path, $size, $sha, $received, $status, $now)
            ON CONFLICT(transfer_id) DO UPDATE SET
                size = excluded.size,
                sha256 = excluded.sha256,
                received_json = excluded.received_json,
                status = excluded.status,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", record.TransferId);
        command.Parameters.AddWithValue("$direction", record.Direction);
        command.Parameters.AddWithValue("$workspace", record.WorkspaceId);
        command.Parameters.AddWithValue("$path", record.RelPath);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$sha", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$received", record.ReceivedJson);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ClientTransferRecord?> GetTransferAsync(string transferId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transfer_id, direction, workspace_id, rel_path, size, sha256, received_json, status
            FROM transfers WHERE transfer_id = $id;
            """;
        command.Parameters.AddWithValue("$id", transferId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadTransfer(reader) : null;
    }

    public async Task<IReadOnlyList<ClientTransferRecord>> ListActiveTransfersAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transfer_id, direction, workspace_id, rel_path, size, sha256, received_json, status
            FROM transfers WHERE status NOT IN ('completed', 'failed') ORDER BY updated_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<ClientTransferRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadTransfer(reader));
        }

        return results;
    }

    public async Task DeleteTransferAsync(string transferId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transfers WHERE transfer_id = $id;";
        command.Parameters.AddWithValue("$id", transferId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ClientTransferRecord ReadTransfer(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));

    private static async Task SetValueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        string now,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO kv (key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<MachineId?> ReadMachineIdAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM kv WHERE key = 'machine_id';";
        var value = await select.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return string.IsNullOrEmpty(value) ? null : new MachineId(value);
    }
}

public sealed record ClientTransferRecord(
    string TransferId,
    string Direction,
    string WorkspaceId,
    string RelPath,
    long Size,
    string? Sha256,
    string ReceivedJson,
    string Status);
