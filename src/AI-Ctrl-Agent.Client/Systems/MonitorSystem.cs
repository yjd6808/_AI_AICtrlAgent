using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AiCtrl.Client.Diagnostics;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed class MonitorSystem : IAcSystem
{
    private readonly IProviderInstanceRegistry _registry;
    private readonly MonitorOptions _options;
    private readonly TimeProvider _time;
    private readonly ErrorClassifier _classifier;
    private readonly AiOutputLog _aiOutput;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<SessionRef, SessionWatch> _watches = new();
    private AcSystemContext? _ctx;

    public MonitorSystem(
        IProviderInstanceRegistry registry,
        MonitorOptions options,
        TimeProvider time,
        ILoggerFactory logs,
        ErrorClassifier? classifier = null)
    {
        _registry = registry;
        _options = options;
        _time = time;
        _classifier = classifier ?? new ErrorClassifier();
        _logger = logs.CreateLogger<MonitorSystem>();
        _aiOutput = new AiOutputLog(logs.CreateLogger("AiOutput"), options);
    }

    public string Name => "monitor";

    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("InitAsync must be called before StartAsync.");

        foreach (var instance in _registry.Instances)
        {
            var captured = instance;
            ctx.Loops.Run("monitor:" + captured.Info.Id.Value, token => WatchInstanceAsync(captured, token));
        }

        ctx.Loops.Run("monitor:stall", StallLoopAsync);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken ct)
    {
        _watches.Clear();
        return ValueTask.CompletedTask;
    }

    private async ValueTask WatchInstanceAsync(IProviderInstance instance, CancellationToken ct)
    {
        await foreach (var providerEvent in instance.WatchAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await HandleEventAsync(instance, providerEvent, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitor failed to handle event {Kind}.", providerEvent.Kind);
            }
        }
    }

    private async ValueTask HandleEventAsync(IProviderInstance instance, ProviderEvent providerEvent, CancellationToken ct)
    {
        if (providerEvent.Kind == ProviderEventKinds.SnapshotRequired)
        {
            await ReconcileAsync(instance, ct).ConfigureAwait(false);
            return;
        }

        if (providerEvent.Data is not { ValueKind: JsonValueKind.Object } props)
        {
            return;
        }

        switch (providerEvent.Kind)
        {
            case "message.updated":
                await HandleMessageUpdatedAsync(instance, props, ct).ConfigureAwait(false);
                break;
            case "message.part.updated":
                HandlePartUpdated(instance, props);
                break;
            case "session.status":
                await HandleSessionStatusAsync(instance, props, ct).ConfigureAwait(false);
                break;
            case "session.idle":
                if (Str(props, "sessionID") is { } idleSessionId)
                {
                    await ApplyRunStateAsync(instance, idleSessionId, AcRunState.Idle, null, null, ct).ConfigureAwait(false);
                }

                break;
            case "session.error":
                await HandleSessionErrorAsync(instance, props, ct).ConfigureAwait(false);
                break;
            case "permission.asked":
                await HandlePermissionAskedAsync(instance, props, ct).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask HandleMessageUpdatedAsync(IProviderInstance instance, JsonElement props, CancellationToken ct)
    {
        var sessionId = Str(props, "sessionID");
        if (sessionId is null || Obj(props, "info") is not { } info)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var messageId = Str(info, "id") ?? string.Empty;
        var role = Str(info, "role") ?? string.Empty;
        var completedAt = TimeMs(info, "completed");
        var watch = GetWatch(Ref(instance, sessionId));
        lock (watch)
        {
            watch.Touch(now);
            if (messageId.Length > 0 && role.Length > 0)
            {
                watch.RememberRole(messageId, role);
            }

            if (role == "assistant")
            {
                watch.LastAssistantCompleted = completedAt is not null;
                watch.LastAssistantMessageId = messageId;
                watch.LastModel = ParseModelRef(info);
            }
        }

        await PublishAsync(
            new MessageUpsertedEvent
            {
                EventId = NewEventId(),
                OccurredAt = now,
                Session = Ref(instance, sessionId),
                Message = new MessageView(
                    messageId,
                    role,
                    TimeMs(info, "created") ?? now,
                    completedAt,
                    ParseModelRef(info),
                    Array.Empty<PartView>(),
                    null),
            },
            ct).ConfigureAwait(false);
    }

    private void HandlePartUpdated(IProviderInstance instance, JsonElement props)
    {
        var sessionId = Str(props, "sessionID");
        if (sessionId is null || Obj(props, "part") is not { } part)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var partId = Str(part, "id") ?? string.Empty;
        var type = Str(part, "type") ?? string.Empty;
        var messageId = Str(part, "messageID") ?? string.Empty;
        var sessionRef = Ref(instance, sessionId);
        var watch = GetWatch(sessionRef);
        lock (watch)
        {
            watch.Touch(now);
        }

        switch (type)
        {
            case "tool":
                if (PartTool(part) is { } tool && tool.Status is "completed" or "error" && partId.Length > 0)
                {
                    if (MarkLogged(watch, partId))
                    {
                        _aiOutput.ToolCompleted(sessionRef, partId, tool);
                    }
                }

                break;
            case "text":
                if (Str(part, "text") is { } text)
                {
                    var role = watch.RoleOf(messageId);
                    if (role == "user")
                    {
                        if (partId.Length > 0 && MarkLogged(watch, partId))
                        {
                            _aiOutput.UserPrompt(sessionRef, messageId, text);
                        }
                    }
                    else if (role == "assistant" && PartEnded(part) && partId.Length > 0 && MarkLogged(watch, partId))
                    {
                        _aiOutput.AssistantText(sessionRef, messageId, watch.LastModel, text);
                    }
                }

                break;
            case "reasoning":
                if (_options.LogReasoning && Str(part, "text") is { } reasoning && PartEnded(part) && partId.Length > 0 && MarkLogged(watch, partId))
                {
                    _aiOutput.Reasoning(sessionRef, messageId, reasoning);
                }

                break;
            case "subtask":
                if (partId.Length > 0 && MarkLogged(watch, partId))
                {
                    _aiOutput.Subtask(sessionRef, partId, Str(part, "description") ?? Str(part, "prompt"));
                }

                break;
        }
    }

    private async ValueTask HandleSessionStatusAsync(IProviderInstance instance, JsonElement props, CancellationToken ct)
    {
        var sessionId = Str(props, "sessionID");
        if (sessionId is null || Obj(props, "status") is not { } status)
        {
            return;
        }

        var state = Str(status, "type") switch
        {
            "idle" => AcRunState.Idle,
            "busy" => AcRunState.Busy,
            "retry" => AcRunState.Retrying,
            _ => AcRunState.Unknown,
        };
        var retry = state == AcRunState.Retrying ? ParseRetry(status) : null;
        var retryReason = state == AcRunState.Retrying ? RetryReason(status) : null;
        await ApplyRunStateAsync(instance, sessionId, state, retry, retryReason, ct).ConfigureAwait(false);
    }

    private async ValueTask ApplyRunStateAsync(
        IProviderInstance instance,
        string sessionId,
        AcRunState state,
        RetryInfo? retry,
        string? retryReason,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var sessionRef = Ref(instance, sessionId);
        var watch = GetWatch(sessionRef);

        AcRunState previous;
        DateTimeOffset? busySince;
        bool turnCompleted;
        string? lastMessageId;
        lock (watch)
        {
            (previous, busySince) = watch.ApplyState(state, now);
            turnCompleted = previous == AcRunState.Busy && state == AcRunState.Idle && watch.LastAssistantCompleted;
            lastMessageId = watch.LastAssistantMessageId;
        }

        await PublishAsync(
            new SessionStatusChangedEvent
            {
                EventId = NewEventId(),
                OccurredAt = now,
                Session = sessionRef,
                RunState = state,
                Retry = retry,
            },
            ct).ConfigureAwait(false);

        if (turnCompleted)
        {
            await PublishAsync(
                new SessionTurnCompletedEvent
                {
                    EventId = NewEventId(),
                    OccurredAt = now,
                    Session = sessionRef,
                    MessageId = lastMessageId,
                    Duration = busySince is { } since ? now - since : null,
                },
                ct).ConfigureAwait(false);

            await FlushTurnAsync(instance, sessionRef, sessionId, watch, ct).ConfigureAwait(false);
        }

        if (retry is not null)
        {
            var classified = _classifier.ClassifyRetry(retryReason, retry.Message);
            if (classified.Class is ErrorClass.QuotaExceeded or ErrorClass.RateLimited)
            {
                _aiOutput.Error(sessionRef, classified);
                await PublishAsync(
                    new SessionErrorClassifiedEvent
                    {
                        EventId = NewEventId(),
                        OccurredAt = now,
                        Session = sessionRef,
                        Error = classified,
                    },
                    ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask FlushTurnAsync(
        IProviderInstance instance,
        SessionRef sessionRef,
        string sessionId,
        SessionWatch watch,
        CancellationToken ct)
    {
        var page = await instance.GetMessagesAsync(sessionId, new PageRequest(null, 200), ct).ConfigureAwait(false);
        var message = page.Items.LastOrDefault(item => item.Role == "assistant" && item.CompletedAt is not null);
        if (message is null)
        {
            return;
        }

        foreach (var part in message.Parts)
        {
            if (part.PartId.Length == 0 || watch.IsPartLogged(part.PartId))
            {
                continue;
            }

            switch (part.Type)
            {
                case "text" when part.Text is not null:
                    watch.MarkPartLogged(part.PartId);
                    _aiOutput.AssistantText(sessionRef, message.MessageId, message.Model, part.Text);
                    break;
                case "reasoning" when part.Text is not null:
                    watch.MarkPartLogged(part.PartId);
                    _aiOutput.Reasoning(sessionRef, message.MessageId, part.Text);
                    break;
                case "tool" when part.Tool is { Status: "completed" or "error" } tool:
                    watch.MarkPartLogged(part.PartId);
                    _aiOutput.ToolCompleted(sessionRef, part.PartId, tool);
                    break;
            }
        }

        if (message.Error is { } error)
        {
            _aiOutput.Error(sessionRef, error);
        }
    }

    private async ValueTask HandleSessionErrorAsync(IProviderInstance instance, JsonElement props, CancellationToken ct)
    {
        var sessionId = Str(props, "sessionID");
        if (sessionId is null || Obj(props, "error") is not { } errorElement)
        {
            return;
        }

        var error = ParseError(errorElement, errorElement.GetRawText());
        var sessionRef = Ref(instance, sessionId);
        _aiOutput.Error(sessionRef, error);

        if (error.Class is ErrorClass.QuotaExceeded or ErrorClass.RateLimited)
        {
            await PublishAsync(
                new SessionErrorClassifiedEvent
                {
                    EventId = NewEventId(),
                    OccurredAt = _time.GetUtcNow(),
                    Session = sessionRef,
                    Error = error,
                },
                ct).ConfigureAwait(false);
        }
    }

    private async ValueTask HandlePermissionAskedAsync(IProviderInstance instance, JsonElement props, CancellationToken ct)
    {
        var sessionId = Str(props, "sessionID");
        var permissionId = Str(props, "id");
        if (sessionId is null || permissionId is null)
        {
            return;
        }

        var patterns = new List<string>();
        if (props.TryGetProperty("patterns", out var patternsElement) && patternsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in patternsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } pattern)
                {
                    patterns.Add(pattern);
                }
            }
        }

        await PublishAsync(
            new SessionPermissionRequestedEvent
            {
                EventId = NewEventId(),
                OccurredAt = _time.GetUtcNow(),
                Session = Ref(instance, sessionId),
                PermissionId = permissionId,
                Permission = Str(props, "permission") ?? string.Empty,
                Patterns = patterns,
            },
            ct).ConfigureAwait(false);
    }

    private async ValueTask ReconcileAsync(IProviderInstance instance, CancellationToken ct)
    {
        var sessions = await instance
            .ListSessionsAsync(new SessionQuery(instance.Info.Id, SessionListViews.All, true, null), ct)
            .ConfigureAwait(false);

        foreach (var summary in sessions)
        {
            await ApplyRunStateAsync(instance, summary.Ref.SessionId, summary.RunState, summary.Retry, null, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask StallLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.StallCheckInterval, _time, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            foreach (var pair in _watches)
            {
                TimeSpan stalledFor;
                lock (pair.Value)
                {
                    if (pair.Value.LastRunState is not (AcRunState.Busy or AcRunState.Retrying) || pair.Value.StallReported)
                    {
                        continue;
                    }

                    stalledFor = now - pair.Value.LastEventAt;
                    if (stalledFor < _options.StallThreshold)
                    {
                        continue;
                    }

                    pair.Value.MarkStallReported();
                }

                _logger.LogWarning("Session {Session} stalled for {Stalled}.", pair.Key.SessionId, stalledFor);
                await PublishAsync(
                    new SessionStalledEvent
                    {
                        EventId = NewEventId(),
                        OccurredAt = now,
                        Session = pair.Key,
                        StalledFor = stalledFor,
                    },
                    ct).ConfigureAwait(false);
            }
        }
    }

    private SessionWatch GetWatch(SessionRef session) => _watches.GetOrAdd(session, key => new SessionWatch(_time.GetUtcNow()));

    private SessionRef Ref(IProviderInstance instance, string sessionId) =>
        new(_registry.MachineId, instance.Info.Id, sessionId);

    private ValueTask PublishAsync(AcEvent e, CancellationToken ct)
    {
        var bus = _ctx?.Bus;
        return bus is null ? ValueTask.CompletedTask : bus.PublishAsync(e, ct);
    }

    private static bool MarkLogged(SessionWatch watch, string partId)
    {
        lock (watch)
        {
            if (watch.IsPartLogged(partId))
            {
                return false;
            }

            watch.MarkPartLogged(partId);
            return true;
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

    private static RetryInfo? ParseRetry(JsonElement status) =>
        new(
            (int)(Long(status, "attempt") ?? 0),
            Str(status, "message"),
            Long(status, "next") is { } next ? DateTimeOffset.FromUnixTimeMilliseconds(next) : null);

    private static string? RetryReason(JsonElement status) =>
        Obj(status, "action") is { } action ? Str(action, "reason") : null;

    private ErrorInfo ParseError(JsonElement error, string? rawJson)
    {
        var name = Str(error, "name");
        var data = Obj(error, "data");
        var message = data is { } dataValue ? Str(dataValue, "message") : null;
        int? statusCode = data is { } codeValue && Long(codeValue, "statusCode") is { } code ? (int)code : null;
        return _classifier.Classify(name, message, statusCode, rawJson);
    }

    private static ModelRef? ParseModelRef(JsonElement info)
    {
        var modelId = Str(info, "modelID");
        var providerId = Str(info, "providerID");
        if (modelId is not null && providerId is not null)
        {
            return new ModelRef(providerId, modelId);
        }

        return Obj(info, "model") is { } model
            ? ModelRefOf(model)
            : null;
    }

    private static ModelRef? ModelRefOf(JsonElement model)
    {
        var id = Str(model, "id") ?? Str(model, "modelID");
        var providerId = Str(model, "providerID");
        return id is not null && providerId is not null ? new ModelRef(providerId, id) : null;
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

    private static string NewEventId() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
