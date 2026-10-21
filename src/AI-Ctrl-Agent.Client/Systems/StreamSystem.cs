using System.Collections.Concurrent;
using System.Text.Json;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed class StreamSystem : IAcSystem
{
    private const int MaxSessionIdLength = 128;

    private const int MaxTextChunkChars = 4000;

    private const int MaxRoleEntries = 512;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProviderInstanceRegistry _registry;

    private readonly ProviderEventBroker _events;

    private readonly AgentPushChannel _push;

    private readonly TimeProvider _time;

    private readonly long _epochBase;

    private readonly ILogger<StreamSystem> _logger;

    private readonly object _gate = new();

    private readonly Dictionary<string, SessionWatch> _watches = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<PendingChunk> _pending = new();

    private AcSystemContext? _ctx;

    private IDisposable? _subscription;

    public StreamSystem(
        IProviderInstanceRegistry registry,
        ProviderEventBroker events,
        AgentPushChannel push,
        TimeProvider time,
        ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(push);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logs);
        _registry = registry;
        _events = events;
        _push = push;
        _time = time;
        _epochBase = time.GetUtcNow().ToUnixTimeMilliseconds();
        _logger = logs.CreateLogger<StreamSystem>();
    }

    public string Name => "stream";

    public IReadOnlyList<string> DependsOn { get; } = new[] { "monitor" };

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _subscription ??= _events.Subscribe(OnProviderEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("InitAsync must be called before StartAsync.");
        ctx.Loops.Run("stream:flush", FlushLoopAsync);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        _subscription = null;
        ClearWatches();
        return ValueTask.CompletedTask;
    }

    public void SetWatchedSessions(IReadOnlyList<SessionRef> sessions, int leaseSeconds)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!string.Equals(session.Machine.Value, _registry.MachineId.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(session.SessionId) || session.SessionId.Length > MaxSessionIdLength)
            {
                continue;
            }

            wanted.Add(session.SessionId);
        }

        var now = _time.GetUtcNow();
        var until = now.AddSeconds(Math.Clamp(leaseSeconds, 1, 300));
        lock (_gate)
        {
            foreach (var pair in _watches.ToArray())
            {
                if (!wanted.Contains(pair.Key))
                {
                    lock (pair.Value.Gate)
                    {
                        DeactivateLocked(pair.Value);
                    }
                }
            }

            foreach (var sessionId in wanted)
            {
                if (!_watches.TryGetValue(sessionId, out var watch))
                {
                    watch = new SessionWatch();
                    _watches.Add(sessionId, watch);
                }

                lock (watch.Gate)
                {
                    if (!watch.Active || watch.LeaseUntil <= now)
                    {
                        watch.Epoch = Math.Max(watch.Epoch, _epochBase) + 1;
                        watch.NextSeq = 0;
                        watch.Parts.Clear();
                        watch.Roles.Clear();
                    }

                    watch.Active = true;
                    watch.LeaseUntil = until;
                }
            }
        }
    }

    public void ClearWatches()
    {
        SessionWatch[] watches;
        lock (_gate)
        {
            watches = _watches.Values.ToArray();
        }

        foreach (var watch in watches)
        {
            lock (watch.Gate)
            {
                DeactivateLocked(watch);
            }
        }

        while (_pending.TryDequeue(out _))
        {
        }
    }

    public async Task<int> FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        Expire(now);

        var batches = new List<PendingBatch>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        while (_pending.TryDequeue(out var pending))
        {
            SessionWatch? watch;
            lock (_gate)
            {
                _watches.TryGetValue(pending.SessionId, out watch);
            }

            if (watch is null)
            {
                continue;
            }

            lock (watch.Gate)
            {
                if (!watch.Active || watch.LeaseUntil <= now || pending.Epoch != watch.Epoch)
                {
                    _logger.LogTrace(
                        "Dropped stale stream chunk {Seq} for {SessionId}.",
                        pending.Chunk.Seq,
                        pending.SessionId);
                    continue;
                }
            }

            if (!indexes.TryGetValue(pending.SessionId, out var index))
            {
                index = batches.Count;
                indexes.Add(pending.SessionId, index);
                batches.Add(new PendingBatch(pending.SessionId, pending.Epoch));
            }

            batches[index].Chunks.Add(pending.Chunk);
        }

        var sent = 0;
        foreach (var batch in batches)
        {
            var streamBatch = new StreamBatch(_registry.MachineId, batch.SessionId, batch.Epoch, batch.Chunks);
            if (await _push.TrySendAsync(streamBatch, ct).ConfigureAwait(false))
            {
                sent++;
            }
        }

        return sent;
    }

    private void OnProviderEvent(IProviderInstance instance, ProviderEvent providerEvent)
    {
        if (providerEvent.Data is not { ValueKind: JsonValueKind.Object } props)
        {
            return;
        }

        var sessionId = Str(props, "sessionID");
        if (sessionId is null)
        {
            return;
        }

        SessionWatch? watch;
        lock (_gate)
        {
            _watches.TryGetValue(sessionId, out watch);
        }

        if (watch is null)
        {
            return;
        }

        lock (watch.Gate)
        {
            var now = _time.GetUtcNow();
            if (!watch.Active || watch.LeaseUntil <= now)
            {
                DeactivateLocked(watch);
                return;
            }

            switch (providerEvent.Kind)
            {
                case "message.updated":
                    RememberRole(watch, props);
                    break;
                case "message.part.updated":
                    HandlePart(watch, sessionId, props);
                    break;
                case "message.part.delta":
                    HandlePartDelta(watch, sessionId, props);
                    break;
            }
        }
    }

    private static void RememberRole(SessionWatch watch, JsonElement props)
    {
        if (Obj(props, "info") is not { } info)
        {
            return;
        }

        var messageId = Str(info, "id");
        var role = Str(info, "role");
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(role))
        {
            return;
        }

        if (watch.Roles.Count >= MaxRoleEntries && !watch.Roles.ContainsKey(messageId))
        {
            var oldest = watch.Roles.Keys.FirstOrDefault();
            if (oldest is not null)
            {
                watch.Roles.Remove(oldest);
            }
        }

        watch.Roles[messageId] = role;
    }

    private void HandlePart(SessionWatch watch, string sessionId, JsonElement props)
    {
        if (Obj(props, "part") is not { } part)
        {
            return;
        }

        var partId = Str(part, "id");
        var messageId = Str(part, "messageID");
        var type = Str(part, "type");
        if (string.IsNullOrEmpty(partId) || string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(type))
        {
            return;
        }

        if (!watch.Parts.TryGetValue(partId, out var state))
        {
            state = new PartState();
            watch.Parts.Add(partId, state);
        }

        state.MessageId = messageId;
        state.PartType = type;

        switch (type)
        {
            case "text":
                if (watch.Roles.TryGetValue(messageId, out var role) &&
                    string.Equals(role, "user", StringComparison.Ordinal))
                {
                    return;
                }

                if (Str(part, "text") is { } text)
                {
                    EmitText(watch, sessionId, messageId, partId, type, text, state, PartEnded(part));
                }

                break;
            case "reasoning":
                if (Str(part, "text") is { } reasoning)
                {
                    EmitText(watch, sessionId, messageId, partId, type, reasoning, state, PartEnded(part));
                }

                break;
            case "tool":
                if (PartTool(part) is { } tool)
                {
                    EmitTool(watch, sessionId, messageId, partId, tool, state, PartEnded(part));
                }

                break;
        }
    }

    private void HandlePartDelta(SessionWatch watch, string sessionId, JsonElement props)
    {
        var partId = Str(props, "partID");
        var messageId = Str(props, "messageID");
        var field = Str(props, "field");
        var delta = Str(props, "delta");
        if (string.IsNullOrEmpty(partId) ||
            string.IsNullOrEmpty(messageId) ||
            !string.Equals(field, "text", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(delta) ||
            !watch.Parts.TryGetValue(partId, out var state) ||
            !string.Equals(state.MessageId, messageId, StringComparison.Ordinal) ||
            state.Done ||
            state.PartType is not ("text" or "reasoning"))
        {
            return;
        }

        if (state.PartType == "text" &&
            watch.Roles.TryGetValue(messageId, out var role) &&
            string.Equals(role, "user", StringComparison.Ordinal))
        {
            return;
        }

        var current = state.LastText ?? string.Empty;
        if (!state.DeltaSeen &&
            current.Length > 0 &&
            current.EndsWith(delta, StringComparison.Ordinal))
        {
            state.DeltaSeen = true;
            return;
        }

        state.DeltaSeen = true;
        var op = current.Length == 0 ? StreamOp.Replace : StreamOp.Append;
        AddTextChunks(watch, sessionId, messageId, partId, state.PartType, op, delta);
        state.LastText = string.Concat(current, delta);
    }

    private void EmitText(
        SessionWatch watch,
        string sessionId,
        string messageId,
        string partId,
        string partType,
        string text,
        PartState state,
        bool ended)
    {
        if (state.DeltaSeen && !ended)
        {
            return;
        }

        if (!string.Equals(state.LastText, text, StringComparison.Ordinal))
        {
            if (state.LastText is { Length: > 0 } previous &&
                text.StartsWith(previous, StringComparison.Ordinal))
            {
                AddTextChunks(watch, sessionId, messageId, partId, partType, StreamOp.Append, text[previous.Length..]);
            }
            else
            {
                AddTextChunks(watch, sessionId, messageId, partId, partType, StreamOp.Replace, text);
            }

            state.LastText = text;
        }

        if (ended && !state.Done)
        {
            state.Done = true;
            Enqueue(watch, sessionId, messageId, partId, partType, StreamOp.PartDone, null, null);
        }
    }

    private void AddTextChunks(
        SessionWatch watch,
        string sessionId,
        string messageId,
        string partId,
        string partType,
        StreamOp op,
        string text)
    {
        var offset = 0;
        var first = true;
        while (offset < text.Length)
        {
            var length = Math.Min(MaxTextChunkChars, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }

            var chunk = text.Substring(offset, length);
            Enqueue(watch, sessionId, messageId, partId, partType, first ? op : StreamOp.Append, chunk, null);
            first = false;
            offset += length;
        }
    }

    private void EmitTool(
        SessionWatch watch,
        string sessionId,
        string messageId,
        string partId,
        ToolPartView tool,
        PartState state,
        bool ended)
    {
        var signature = string.Join(
            '\u001f',
            tool.Tool,
            tool.Status,
            tool.Title ?? string.Empty,
            tool.OutputPreview ?? string.Empty,
            tool.Input ?? string.Empty);

        if (!string.Equals(state.ToolSignature, signature, StringComparison.Ordinal))
        {
            state.ToolSignature = signature;
            Enqueue(watch, sessionId, messageId, partId, "tool", StreamOp.ToolState, null, tool);
        }

        if ((ended || tool.CompletedAt is not null) && !state.Done)
        {
            state.Done = true;
            Enqueue(watch, sessionId, messageId, partId, "tool", StreamOp.PartDone, null, null);
        }
    }

    private void Enqueue(
        SessionWatch watch,
        string sessionId,
        string messageId,
        string partId,
        string partType,
        StreamOp op,
        string? text,
        ToolPartView? tool)
    {
        var chunk = new StreamChunk(messageId, partId, partType, ++watch.NextSeq, op, text, tool);
        _pending.Enqueue(new PendingChunk(sessionId, watch.Epoch, chunk));
    }

    private void Expire(DateTimeOffset now)
    {
        SessionWatch[] watches;
        lock (_gate)
        {
            watches = _watches.Values.ToArray();
        }

        foreach (var watch in watches)
        {
            lock (watch.Gate)
            {
                if (watch.Active && watch.LeaseUntil <= now)
                {
                    DeactivateLocked(watch);
                }
            }
        }
    }

    private static void DeactivateLocked(SessionWatch watch)
    {
        watch.Active = false;
        watch.LeaseUntil = default;
        watch.Parts.Clear();
        watch.Roles.Clear();
    }

    private async ValueTask FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(FlushInterval, _time, ct).ConfigureAwait(false);
            await FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool PartEnded(JsonElement part) => TimeMs(part, "end") is not null;

    private static ToolPartView? PartTool(JsonElement part)
    {
        var state = Obj(part, "state");
        if (state is not { } stateValue)
        {
            return null;
        }

        return new ToolPartView(
            Str(part, "tool") ?? string.Empty,
            Str(stateValue, "status") ?? "unknown",
            Str(stateValue, "title"),
            Preview(Str(stateValue, "output")),
            Str(stateValue, "input"),
            TimeMs(stateValue, "start"),
            TimeMs(stateValue, "end"));
    }

    private static string? Preview(string? text) =>
        text is null ? null : text.Length <= 200 ? text : text[..200];

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static JsonElement? Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static DateTimeOffset? TimeMs(JsonElement element, string name) =>
        Obj(element, "time") is { } time && Long(time, name) is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    private sealed class SessionWatch
    {
        public object Gate { get; } = new();

        public long Epoch { get; set; }

        public long NextSeq { get; set; }

        public bool Active { get; set; }

        public DateTimeOffset LeaseUntil { get; set; }

        public Dictionary<string, string> Roles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, PartState> Parts { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PartState
    {
        public string MessageId { get; set; } = string.Empty;

        public string PartType { get; set; } = string.Empty;

        public string? LastText { get; set; }

        public string? ToolSignature { get; set; }

        public bool DeltaSeen { get; set; }

        public bool Done { get; set; }
    }

    private sealed record PendingChunk(string SessionId, long Epoch, StreamChunk Chunk);

    private sealed class PendingBatch
    {
        public PendingBatch(string sessionId, long epoch)
        {
            SessionId = sessionId;
            Epoch = epoch;
        }

        public string SessionId { get; }

        public long Epoch { get; }

        public List<StreamChunk> Chunks { get; } = new();
    }
}
