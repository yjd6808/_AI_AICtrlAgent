using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AiCtrl.Server.Streams;

public sealed record StreamReadResult(
    long Epoch,
    string NextCursor,
    bool CursorExpired,
    IReadOnlyList<StreamChunk> Chunks);

public sealed class SessionStreamHub
{
    private readonly ConcurrentDictionary<StreamKey, StreamState> _sessions = new();

    private readonly StreamOptions _options;

    private readonly TimeProvider _time;

    private readonly IHubContext<UiHub, IUiClient> _ui;

    private readonly ILogger<SessionStreamHub> _logger;

    public SessionStreamHub(
        StreamOptions options,
        TimeProvider time,
        IHubContext<UiHub, IUiClient> ui,
        ILogger<SessionStreamHub> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _time = time;
        _ui = ui;
        _logger = logger;
    }

    public async Task PublishAsync(StreamBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ct.ThrowIfCancellationRequested();
        var key = Key(batch.Machine.Value, batch.SessionId);
        var state = _sessions.GetOrAdd(key, _ => new StreamState(_time.GetUtcNow()));
        var now = _time.GetUtcNow();
        var accepted = new List<StreamChunk>();
        TaskCompletionSource? signal = null;
        var changed = false;

        lock (state.Gate)
        {
            state.LastTouched = now;
            if (batch.Epoch < state.Epoch)
            {
                return;
            }

            if (batch.Epoch > state.Epoch)
            {
                state.Epoch = batch.Epoch;
                state.LastSeq = 0;
                state.Bytes = 0;
                state.Chunks.Clear();
                changed = true;
            }

            foreach (var chunk in batch.Chunks)
            {
                if (chunk.Seq <= state.LastSeq)
                {
                    continue;
                }

                var size = EstimateBytes(chunk);
                if (size > _options.EffectiveRingBytes)
                {
                    _logger.LogWarning(
                        "Dropped stream chunk {Seq} larger than the ring byte limit.",
                        chunk.Seq);
                    continue;
                }

                state.Chunks.Add(chunk);
                state.Bytes += size;
                state.LastSeq = chunk.Seq;
                accepted.Add(chunk);
                changed = true;
            }

            Trim(state);
            if (changed)
            {
                signal = state.Signal;
                state.Signal = NewSignal();
            }
        }

        signal?.TrySetResult();
        if (accepted.Count == 0)
        {
            return;
        }

        try
        {
            await _ui.Clients
                .Group(UiHub.SessionGroup(batch.Machine, batch.SessionId))
                .StreamChunks(batch.Machine, batch.SessionId, batch.Epoch, accepted)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast stream chunks for {SessionId}.", batch.SessionId);
        }
    }

    public async Task<StreamReadResult> ReadAsync(
        string machineId,
        string sessionId,
        string? cursor,
        TimeSpan wait,
        CancellationToken ct)
    {
        var key = Key(machineId, sessionId);
        var state = _sessions.GetOrAdd(key, _ => new StreamState(_time.GetUtcNow()));
        var hasCursor = !string.IsNullOrWhiteSpace(cursor);
        long epoch = 0;
        long seq = 0;
        var validCursor = hasCursor && TryParseCursor(cursor, out epoch, out seq);

        var maxWait = wait <= TimeSpan.Zero
            ? TimeSpan.Zero
            : wait > _options.LongPollMax ? _options.LongPollMax : wait;
        var deadline = _time.GetUtcNow() + maxWait;

        while (true)
        {
            Task signal;
            lock (state.Gate)
            {
                state.LastTouched = _time.GetUtcNow();
                if (TryReadLocked(state, hasCursor, validCursor, epoch, seq, waitAllowed: true, out var result))
                {
                    return result;
                }

                signal = state.Signal.Task;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                lock (state.Gate)
                {
                    _ = TryReadLocked(state, hasCursor, validCursor, epoch, seq, waitAllowed: false, out var result);
                    return result;
                }
            }

            var delay = Task.Delay(remaining, _time, ct);
            var completed = await Task.WhenAny(signal, delay).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (completed == delay)
            {
                lock (state.Gate)
                {
                    _ = TryReadLocked(state, hasCursor, validCursor, epoch, seq, waitAllowed: false, out var result);
                    return result;
                }
            }
        }
    }

    public void SweepIdle()
    {
        var now = _time.GetUtcNow();
        var collection = (ICollection<KeyValuePair<StreamKey, StreamState>>)_sessions;
        foreach (var pair in _sessions.ToArray())
        {
            if (now - pair.Value.LastTouched >= _options.IdleDrop)
            {
                collection.Remove(pair);
            }
        }
    }

    private static bool TryReadLocked(
        StreamState state,
        bool hasCursor,
        bool validCursor,
        long epoch,
        long seq,
        bool waitAllowed,
        out StreamReadResult result)
    {
        var expired = false;
        if (!hasCursor)
        {
            result = Snapshot(state, cursorExpired: false);
            return true;
        }

        if (!validCursor || epoch != state.Epoch || seq > state.LastSeq)
        {
            expired = true;
        }
        else if (state.Chunks.Count > 0 && seq < state.Chunks[0].Seq - 1)
        {
            expired = true;
        }

        if (expired)
        {
            result = Snapshot(state, cursorExpired: true);
            return true;
        }

        if (seq == state.LastSeq)
        {
            if (waitAllowed)
            {
                result = default!;
                return false;
            }

            result = new StreamReadResult(
                state.Epoch,
                Cursor(state.Epoch, seq),
                CursorExpired: false,
                Array.Empty<StreamChunk>());
            return true;
        }

        var chunks = state.Chunks.Where(chunk => chunk.Seq > seq).ToList();
        result = new StreamReadResult(
            state.Epoch,
            Cursor(state.Epoch, state.LastSeq),
            CursorExpired: false,
            chunks);
        return true;
    }

    private static StreamReadResult Snapshot(StreamState state, bool cursorExpired)
    {
        var chunks = state.Chunks.ToArray();
        return new StreamReadResult(
            state.Epoch,
            Cursor(state.Epoch, state.LastSeq),
            cursorExpired,
            chunks);
    }

    private void Trim(StreamState state)
    {
        while (state.Chunks.Count > _options.EffectiveRingChunks)
        {
            RemoveFirst(state);
        }

        while (state.Bytes > _options.EffectiveRingBytes && state.Chunks.Count > 0)
        {
            RemoveFirst(state);
        }
    }

    private static void RemoveFirst(StreamState state)
    {
        var first = state.Chunks[0];
        state.Chunks.RemoveAt(0);
        state.Bytes -= EstimateBytes(first);
    }

    private static int EstimateBytes(StreamChunk chunk) =>
        (chunk.Text is null ? 0 : Encoding.UTF8.GetByteCount(chunk.Text)) + 128;

    private static bool TryParseCursor(string? cursor, out long epoch, out long seq)
    {
        epoch = 0;
        seq = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == cursor.Length - 1)
        {
            return false;
        }

        return long.TryParse(cursor[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out epoch)
            && long.TryParse(cursor[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    private static string Cursor(long epoch, long seq) =>
        string.Create(CultureInfo.InvariantCulture, $"{epoch}:{seq}");

    private static StreamKey Key(string machineId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }

        return new StreamKey(machineId, sessionId);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct StreamKey(string MachineId, string SessionId);

    private sealed class StreamState
    {
        public StreamState(DateTimeOffset now)
        {
            LastTouched = now;
        }

        public object Gate { get; } = new();

        public long Epoch { get; set; }

        public long LastSeq { get; set; }

        public long Bytes { get; set; }

        public DateTimeOffset LastTouched { get; set; }

        public List<StreamChunk> Chunks { get; } = new();

        public TaskCompletionSource Signal { get; set; } = NewSignal();
    }
}
