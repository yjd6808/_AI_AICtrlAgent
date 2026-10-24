using System.Threading.Channels;

namespace AiCtrl.FakeOpenCode;

public sealed class FakeOpenCodeState
{
    private readonly object _gate = new();
    private readonly FakeOpenCodeScenario _scenario;
    private readonly FakeSseHub _sse;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, SessionEntry> _sessions = new();
    private readonly Dictionary<string, Dictionary<string, object?>> _permissions = new();
    private long _sessionSeq;
    private long _messageSeq;
    private long _partSeq;
    private long _permissionSeq;
    private long _callSeq;

    public FakeOpenCodeState(FakeOpenCodeScenario scenario, FakeSseHub sse, TimeProvider? time = null)
    {
        _scenario = scenario;
        _sse = sse;
        _time = time ?? TimeProvider.System;
    }

    public FakeSseHub Sse => _sse;

    public string SeedSession(string title, string? parentId = null, string directory = @"C:\fake\ws")
    {
        lock (_gate)
        {
            var entry = CreateEntry(title, parentId, directory);
            _sessions[entry.Session.Id] = entry;
            return entry.Session.Id;
        }
    }

    public FakeSessionDto CreateSession(string? title, string? parentId, string? directory)
    {
        lock (_gate)
        {
            var entry = CreateEntry(
                string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
                parentId,
                string.IsNullOrWhiteSpace(directory) ? @"C:\fake\ws" : directory);
            _sessions[entry.Session.Id] = entry;
            _sse.Publish("session.created", new Dictionary<string, object?>
            {
                ["sessionID"] = entry.Session.Id,
                ["info"] = entry.Session,
            });
            return entry.Session;
        }
    }

    public string SeedUserMessage(string sessionId, string text, string? messageId = null)
    {
        lock (_gate)
        {
            var entry = Require(sessionId);
            var id = messageId ?? NextMessageId();
            var info = new FakeMessageDto(id, sessionId, "user", new FakeMessageTimeDto(NowMs(), null), "build", DefaultModel(), null, null, null, null, null, null, null, null, null);
            entry.Messages.Add((info, new List<FakePartDto> { NewTextPart(sessionId, id, text) }));
            Touch(entry);
            _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = info });
            return id;
        }
    }

    public string SeedAssistantMessage(string sessionId, string text, bool completed, FakeErrorDto? error = null)
    {
        lock (_gate)
        {
            var entry = Require(sessionId);
            var id = NextMessageId();
            var now = NowMs();
            var info = NewAssistantMessage(sessionId, id, completed ? now : null, error);
            var parts = new List<FakePartDto> { NewTextPart(sessionId, id, text) };
            entry.Messages.Add((info, parts));
            Touch(entry);
            _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = info });
            return id;
        }
    }

    public string RequestPermission(string sessionId, string permission)
    {
        lock (_gate)
        {
            var entry = Require(sessionId);
            var id = "per_" + (++_permissionSeq).ToString();
            var payload = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["sessionID"] = sessionId,
                ["permission"] = permission,
                ["patterns"] = Array.Empty<string>(),
                ["metadata"] = new Dictionary<string, object?>(),
                ["always"] = Array.Empty<string>(),
                ["tool"] = new Dictionary<string, object?>
                {
                    ["messageID"] = entry.ActiveAssistantMessageId ?? "msg_0",
                    ["callID"] = "call_" + (++_callSeq).ToString(),
                },
            };
            _permissions[id] = payload;
            _sse.Publish("permission.asked", payload);
            return id;
        }
    }

    public bool SessionExists(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(sessionId);
        }
    }

    public FakeSessionDto? GetSession(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var entry) ? entry.Session : null;
        }
    }

    public IReadOnlyList<FakeSessionDto> ListSessions()
    {
        lock (_gate)
        {
            return _sessions.Values.Select(entry => entry.Session).ToList();
        }
    }

    public IReadOnlyList<FakeSessionDto> ListChildren(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.Values
                .Where(entry => string.Equals(entry.Session.ParentId, sessionId, StringComparison.Ordinal))
                .Select(entry => entry.Session)
                .ToList();
        }
    }

    public Dictionary<string, object?> BuildStatusMap()
    {
        lock (_gate)
        {
            return _sessions.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Status);
        }
    }

    public IReadOnlyList<FakeMessageListEntryDto> GetMessages(string sessionId)
    {
        lock (_gate)
        {
            var entry = Require(sessionId);
            return entry.Messages
                .Select(message => new FakeMessageListEntryDto(message.Info, message.Parts.ToList()))
                .ToList();
        }
    }

    public bool AcceptPrompt(string sessionId, string? messageId, string text, string? agent, FakeModelDto? model)
    {
        CancellationToken token;
        string assistantId;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                return false;
            }

            if (!_scenario.DropPromptAcceptance)
            {
                var userId = messageId ?? NextMessageId();
                var userInfo = new FakeMessageDto(userId, sessionId, "user", new FakeMessageTimeDto(NowMs(), null), agent ?? "build", model ?? DefaultModel(), null, null, null, null, null, null, null, null, null);
                entry.Messages.Add((userInfo, new List<FakePartDto> { NewTextPart(sessionId, userId, text) }));
                _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = userInfo });
                var userPart = entry.Messages[^1].Parts[0];
                _sse.Publish("message.part.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["part"] = userPart, ["time"] = NowMs() });
            }

            assistantId = NextMessageId();
            var assistant = NewAssistantMessage(sessionId, assistantId, null, null);
            entry.Messages.Add((assistant, new List<FakePartDto>()));
            entry.ActiveAssistantMessageId = assistantId;
            Touch(entry);
            _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = assistant });
            SetStatus(entry, BusyStatus());
            entry.TurnCts = new CancellationTokenSource();
            token = entry.TurnCts.Token;
        }

        _ = Task.Run(() => RunTurnAsync(sessionId, assistantId, text, token), CancellationToken.None);
        return true;
    }

    public bool Abort(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                return false;
            }

            if (_scenario.ConsumeIgnoreAbort())
            {
                return true;
            }

            entry.TurnCts?.Cancel();
            if (entry.ActiveAssistantMessageId is { } messageId &&
                TryFindMessage(entry, messageId, out var index) &&
                entry.Messages[index].Info.Time.Completed is null)
            {
                var aborted = entry.Messages[index].Info with
                {
                    Time = new FakeMessageTimeDto(entry.Messages[index].Info.Time.Created, NowMs()),
                    Error = AbortError(),
                };
                entry.Messages[index] = (aborted, entry.Messages[index].Parts);
                _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = aborted });
                _sse.Publish("session.error", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["error"] = aborted.Error });
            }

            entry.ActiveAssistantMessageId = null;
            SetStatus(entry, IdleStatus());
            _sse.Publish("session.idle", new Dictionary<string, object?> { ["sessionID"] = sessionId });
            return true;
        }
    }

    public void RecordPermissionReply(string permissionId, string response)
    {
        lock (_gate)
        {
            if (!_permissions.TryGetValue(permissionId, out var payload))
            {
                return;
            }

            _sse.Publish("permission.replied", new Dictionary<string, object?>
            {
                ["sessionID"] = payload["sessionID"],
                ["permissionID"] = permissionId,
                ["response"] = response,
            });
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            foreach (var entry in _sessions.Values)
            {
                entry.TurnCts?.Cancel();
            }
        }
    }

    private async Task RunTurnAsync(string sessionId, string assistantId, string promptText, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_scenario.BusyDuration, _time, ct).ConfigureAwait(false);
            if (_scenario.LoopQuotaRetry)
            {
                var attempt = 1;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (_sessions.TryGetValue(sessionId, out var entry))
                        {
                            SetStatus(entry, RetryStatus(attempt));
                            AttachAssistantError(entry, sessionId, assistantId, QuotaError());
                        }
                    }

                    attempt++;
                    await Task.Delay(_scenario.QuotaRetryDelay, _time, ct).ConfigureAwait(false);
                }
            }

            var full = "Echo: " + promptText;
            var chunks = Math.Max(1, _scenario.ChunkCount);
            var chunkLength = Math.Max(1, (int)Math.Ceiling((double)full.Length / chunks));
            var accumulated = string.Empty;
            string partId;
            lock (_gate)
            {
                partId = NextPartId();
                var entry = Require(sessionId);
                entry.Messages.First(message => message.Info.Id == assistantId).Parts.Add(NewTextPart(sessionId, assistantId, string.Empty) with { Id = partId });
            }

            for (var offset = 0; offset < full.Length; offset += chunkLength)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = full.Substring(offset, Math.Min(chunkLength, full.Length - offset));
                accumulated += chunk;
                PublishTextPart(sessionId, assistantId, partId, accumulated, chunk);
                await Task.Delay(_scenario.ChunkDelay, _time, ct).ConfigureAwait(false);
            }

            CompleteTurn(sessionId, assistantId);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PublishTextPart(string sessionId, string assistantId, string partId, string accumulated, string delta)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry) || !TryFindMessage(entry, assistantId, out var index))
            {
                return;
            }

            var (info, parts) = entry.Messages[index];
            var partIndex = parts.FindIndex(part => part.Id == partId);
            FakePartDto part;
            if (partIndex < 0)
            {
                part = NewTextPart(sessionId, assistantId, accumulated) with { Id = partId };
                parts.Add(part);
            }
            else
            {
                part = parts[partIndex] with { Text = accumulated, Time = new FakePartTimeDto(parts[partIndex].Time!.Start, NowMs()) };
                parts[partIndex] = part;
            }

            Touch(entry);
            _sse.Publish("message.part.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["part"] = part, ["time"] = NowMs() });
            if (_scenario.EmitPartDelta)
            {
                _sse.Publish("message.part.delta", new Dictionary<string, object?>
                {
                    ["sessionID"] = sessionId,
                    ["messageID"] = assistantId,
                    ["partID"] = partId,
                    ["field"] = "text",
                    ["delta"] = delta,
                });
            }
        }
    }

    private void CompleteTurn(string sessionId, string assistantId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry) || !TryFindMessage(entry, assistantId, out var index))
            {
                return;
            }

            var info = entry.Messages[index].Info;
            if (info.Time.Completed is not null)
            {
                return;
            }

            var completed = info with { Time = new FakeMessageTimeDto(info.Time.Created, NowMs()), Finish = "stop" };
            entry.Messages[index] = (completed, entry.Messages[index].Parts);
            entry.ActiveAssistantMessageId = null;
            Touch(entry);
            _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = completed });
            SetStatus(entry, IdleStatus());
            _sse.Publish("session.idle", new Dictionary<string, object?> { ["sessionID"] = sessionId });
        }
    }

    private void AttachAssistantError(SessionEntry entry, string sessionId, string assistantId, FakeErrorDto error)
    {
        if (!TryFindMessage(entry, assistantId, out var index))
        {
            return;
        }

        var updated = entry.Messages[index].Info with { Error = error };
        entry.Messages[index] = (updated, entry.Messages[index].Parts);
        Touch(entry);
        _sse.Publish("message.updated", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["info"] = updated });
        _sse.Publish("session.error", new Dictionary<string, object?> { ["sessionID"] = sessionId, ["error"] = error });
    }

    private void SetStatus(SessionEntry entry, Dictionary<string, object?> status)
    {
        entry.Status = status;
        _sse.Publish("session.status", new Dictionary<string, object?>
        {
            ["sessionID"] = entry.Session.Id,
            ["status"] = status,
        });
    }

    private SessionEntry CreateEntry(string title, string? parentId, string directory)
    {
        var now = NowMs();
        var id = "ses_" + (++_sessionSeq).ToString();
        var session = new FakeSessionDto(
            id,
            "fake-" + _sessionSeq.ToString(),
            "proj_fake",
            directory,
            parentId,
            title,
            DefaultModel(),
            new FakeTimeDto(now, now),
            "1.18.34");
        return new SessionEntry(session);
    }

    private SessionEntry Require(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new InvalidOperationException("Unknown fake session: " + sessionId);

    private static bool TryFindMessage(SessionEntry entry, string messageId, out int index)
    {
        for (var i = 0; i < entry.Messages.Count; i++)
        {
            if (entry.Messages[i].Info.Id == messageId)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private FakeMessageDto NewAssistantMessage(string sessionId, string messageId, long? completedAt, FakeErrorDto? error) =>
        new(
            messageId,
            sessionId,
            "assistant",
            new FakeMessageTimeDto(NowMs(), completedAt),
            "build",
            null,
            null,
            DefaultModel().Id,
            DefaultModel().ProviderId,
            "build",
            @"C:\fake\ws",
            0,
            new FakeTokensDto(10, 20, 0, new FakeCacheDto(0, 0)),
            error,
            null);

    private FakePartDto NewTextPart(string sessionId, string messageId, string text) =>
        new(NextPartId(), sessionId, messageId, "text", text, null, null, null, new FakePartTimeDto(NowMs(), null));

    private void Touch(SessionEntry entry)
    {
        entry.Session = entry.Session with { Time = entry.Session.Time with { Updated = NowMs() } };
    }

    private static FakeModelDto DefaultModel() => new("fake-model", "opencode");

    private static Dictionary<string, object?> IdleStatus() => new() { ["type"] = "idle" };

    private static Dictionary<string, object?> BusyStatus() => new() { ["type"] = "busy" };

    private Dictionary<string, object?> RetryStatus(int attempt) => new()
    {
        ["type"] = "retry",
        ["attempt"] = attempt,
        ["message"] = _scenario.QuotaMessage,
        ["next"] = NowMs() + 1000,
        ["action"] = new Dictionary<string, object?>
        {
            ["reason"] = "quota",
            ["provider"] = "fake",
            ["title"] = "Quota",
            ["message"] = _scenario.QuotaMessage,
            ["label"] = "Retry",
        },
    };

    private FakeErrorDto QuotaError() => new("APIError", new Dictionary<string, object?>
    {
        ["message"] = _scenario.QuotaMessage,
        ["statusCode"] = 429,
        ["isRetryable"] = true,
    });

    private static FakeErrorDto AbortError() => new("MessageAbortedError", new Dictionary<string, object?> { ["message"] = "Aborted" });

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private string NextMessageId() => "msg_" + (++_messageSeq).ToString();

    private string NextPartId() => "prt_" + (++_partSeq).ToString();

    internal sealed class SessionEntry
    {
        public SessionEntry(FakeSessionDto session)
        {
            Session = session;
        }

        public FakeSessionDto Session { get; set; }

        public List<(FakeMessageDto Info, List<FakePartDto> Parts)> Messages { get; } = new();

        public Dictionary<string, object?> Status { get; set; } = new() { ["type"] = "idle" };

        public CancellationTokenSource? TurnCts { get; set; }

        public string? ActiveAssistantMessageId { get; set; }
    }
}
