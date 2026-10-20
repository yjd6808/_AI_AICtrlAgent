using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AiCtrl.Client.Storage;

public sealed record OutboxRecord(long Seq, string Type, DateTimeOffset OccurredAt, string PayloadJson);

public sealed class OutboxStore
{
    private const string NextSeqKey = "outbox.next_seq";

    private const string LastAckedKey = "outbox.last_acked";

    private readonly ClientDb _db;

    private readonly TimeProvider _time;

    public OutboxStore(ClientDb db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _time = time;
    }

    public async Task<OutboxRecord> AppendAsync(string type, string payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payloadJson);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var next = await ReadLongAsync(connection, transaction, NextSeqKey, ct).ConfigureAwait(false) + 1;
        var occurredAt = _time.GetUtcNow();
        await WriteLongAsync(connection, transaction, NextSeqKey, next, occurredAt, ct).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO outbox (seq, type, payload_json, occurred_at) VALUES ($seq, $type, $payload, $occurred);
            """;
        insert.Parameters.AddWithValue("$seq", next);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$occurred", occurredAt.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new OutboxRecord(next, type, occurredAt, payloadJson);
    }

    public async Task<IReadOnlyList<OutboxRecord>> GetUnackedAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var acked = await GetLastAckedAsync(ct).ConfigureAwait(false);
        var records = new List<OutboxRecord>();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT seq, type, occurred_at, payload_json FROM outbox
            WHERE seq > $acked ORDER BY seq LIMIT $limit;
            """;
        select.Parameters.AddWithValue("$acked", acked);
        select.Parameters.AddWithValue("$limit", limit);
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            records.Add(new OutboxRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(3)));
        }

        return records;
    }

    public async Task<long> GetLastAckedAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await ReadLongAsync(connection, null, LastAckedKey, ct).ConfigureAwait(false);
    }

    public async Task AckAsync(long seq, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await ReadLongAsync(connection, transaction, LastAckedKey, ct).ConfigureAwait(false);
        if (seq > current)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM outbox WHERE seq <= $seq;";
            delete.Parameters.AddWithValue("$seq", seq);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await WriteLongAsync(connection, transaction, LastAckedKey, seq, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM kv WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static async Task WriteLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        long value,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO kv (key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
