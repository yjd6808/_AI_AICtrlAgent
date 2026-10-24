using System.Net;
using System.Text.Json;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed class OpenCodeInstance : IProviderInstance
{
    private const int DefaultPageLimit = 50;

    private const int MaxPageLimit = 200;

    private const int V2FetchPageSize = 200;

    private const int V2MaxFetchPages = 50;

    private static readonly Dictionary<string, object?> EmptyBody = new(StringComparer.Ordinal);

    private static readonly JsonElement BusyStatus = JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "busy" });

    private static readonly JsonElement IdleStatus = JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "idle" });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OpenCodeHttpApi _api;
    private readonly OpenCodeMapper _mapper;
    private readonly OpenCodeSseReader _sse;
    private readonly OpenCodeV2EventTranslator _v2;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _sseClient;
    private readonly OpenCodeRoutes _routes;
    private readonly TimeProvider _time;
    private readonly MachineId _machine;

    public OpenCodeInstance(
        MachineId machine,
        string instanceId,
        string baseUrl,
        OpenCodeProbeResult probe,
        OpenCodeHttpApi api,
        OpenCodeMapper mapper,
        OpenCodeSseReader sse,
        HttpClient httpClient,
        HttpClient sseClient,
        TimeProvider time,
        bool managed = false)
    {
        _api = api;
        _mapper = mapper;
        _sse = sse;
        _v2 = new OpenCodeV2EventTranslator(time);
        _httpClient = httpClient;
        _sseClient = sseClient;
        _routes = probe.Routes;
        _time = time;
        _machine = machine;
        Info = new InstanceInfo(
            new InstanceId(instanceId),
            ProviderKind.OpenCode,
            baseUrl,
            probe.Version,
            InstanceMode.SharedStore,
            Healthy: probe.Capabilities != CapabilitySet.None,
            Managed: managed,
            probe.Capabilities);
    }

    public InstanceInfo Info { get; }

    public CapabilitySet Capabilities => Info.Capabilities;

    public async ValueTask<SessionSummary> CreateSessionAsync(SessionCreateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        JsonElement? created;
        if (_routes.IsV2)
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(req.Title))
            {
                body["title"] = req.Title;
            }

            if (!string.IsNullOrWhiteSpace(req.Directory))
            {
                body["location"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["directory"] = req.Directory,
                };
            }

            if (req.Model is { } v2Model)
            {
                body["model"] = ToModelRef(v2Model);
            }

            created = await _api.PostJsonAsync(_routes.Sessions, body, ct).ConfigureAwait(false);
        }
        else
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(req.Title))
            {
                body["title"] = req.Title;
            }

            created = await _api
                .PostJsonAsync(_routes.SessionsForDirectory(req.Directory), body, ct)
                .ConfigureAwait(false);
        }

        if (created is not { ValueKind: JsonValueKind.Object })
        {
            throw new OpenCodeHttpException(
                HttpStatusCode.BadGateway,
                body: null,
                "OpenCode returned an unexpected response for session.create.");
        }

        var session = UnwrapData(created.Value);
        if (session.ValueKind != JsonValueKind.Object)
        {
            throw new OpenCodeHttpException(
                HttpStatusCode.BadGateway,
                body: null,
                "OpenCode returned an unexpected response for session.create.");
        }

        var sessionId = Str(session, "id");
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new OpenCodeHttpException(
                HttpStatusCode.BadGateway,
                body: null,
                "OpenCode did not return a session id for session.create.");
        }

        return _mapper.ParseSessionSummary(
            sessionId,
            session,
            _routes.IsV2 ? IdleStatus : null,
            _machine,
            Info.Id);
    }

    public async ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionQuery q, CancellationToken ct)
    {
        var sessions = await _api.GetJsonAsync(_routes.Sessions, ct).ConfigureAwait(false);
        if (sessions is not { } sessionsValue)
        {
            return Array.Empty<SessionSummary>();
        }

        var statusMap = await ReadStatusMapAsync(sessionsValue, ct).ConfigureAwait(false);
        var summaries = _mapper.ParseSessionSummaries(sessionsValue, statusMap, _machine, Info.Id);

        var includeAll = string.Equals(q.View, SessionListViews.All, StringComparison.Ordinal);
        var filtered = summaries
            .Where(summary => includeAll || summary.ParentId is null)
            .Where(summary => q.IncludeChildren || summary.ParentId is null)
            .OrderByDescending(summary => summary.UpdatedAt)
            .ToList();

        return filtered;
    }

    public async ValueTask<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        JsonElement? session;
        try
        {
            session = await _api.GetJsonAsync(_routes.Session(sessionId), ct).ConfigureAwait(false);
        }
        catch (OpenCodeHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (session is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var sessionValue = UnwrapData(session.Value);
        if (sessionValue.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var status = await ReadSessionStatusAsync(sessionId, ct).ConfigureAwait(false);
        return _mapper.ParseSessionSummary(sessionId, sessionValue, status, _machine, Info.Id);
    }

    public async ValueTask<Page<MessageView>> GetMessagesAsync(string sessionId, PageRequest page, CancellationToken ct)
    {
        var offset = ParseCursor(page.Cursor);
        var limit = page.Limit <= 0 ? DefaultPageLimit : Math.Min(page.Limit, MaxPageLimit);

        var all = _routes.IsV2
            ? await FetchV2MessagesAsync(sessionId, offset + limit, ct).ConfigureAwait(false)
            : await FetchV1MessagesAsync(sessionId, ct).ConfigureAwait(false);

        var slice = all.Skip(offset).Take(limit).ToList();
        var consumed = offset + slice.Count;
        var nextCursor = consumed < all.Count
            ? consumed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new Page<MessageView>(slice, nextCursor, nextCursor is not null);
    }

    public async ValueTask<IReadOnlyList<ModelOption>> ListModelsAsync(CancellationToken ct)
    {
        var providers = await _api.GetJsonAsync(_routes.Providers, ct).ConfigureAwait(false);
        return providers is { } providersValue
            ? _mapper.ParseModels(providersValue)
            : Array.Empty<ModelOption>();
    }

    public async ValueTask<AbortResult> AbortAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var body = await _api.PostJsonAsync(_routes.Abort(sessionId), EmptyBody, ct).ConfigureAwait(false);
            if (_routes.IsV2)
            {
                var interrupted = body is { ValueKind: JsonValueKind.Object } response &&
                                  response.TryGetProperty("interrupted", out var interruptedValue) &&
                                  interruptedValue.ValueKind == JsonValueKind.True;
                return new AbortResult(interrupted, null);
            }

            var aborted = body is not { ValueKind: JsonValueKind.False };
            return new AbortResult(aborted, null);
        }
        catch (OpenCodeHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new AbortResult(false, "Session not found on instance.");
        }
    }

    public async ValueTask<PromptAccepted> PromptAsync(string sessionId, PromptRequest req, CancellationToken ct)
    {
        var messageId = string.IsNullOrEmpty(req.ClientMessageId)
            ? string.Empty
            : NormalizeMessageId(req.ClientMessageId);

        if (_routes.IsV2)
        {
            return await PromptV2Async(sessionId, req, messageId, ct).ConfigureAwait(false);
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["parts"] = new[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "text", ["text"] = req.Text } },
        };

        if (req.Model is { } model)
        {
            body["model"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["providerID"] = model.ProviderId,
                ["modelID"] = model.ModelId,
            };
        }

        if (!string.IsNullOrEmpty(req.Agent))
        {
            body["agent"] = req.Agent;
        }

        if (messageId.Length > 0)
        {
            body["messageID"] = messageId;
        }

        await _api.PostJsonAsync(_routes.PromptAsync(sessionId), body, ct).ConfigureAwait(false);
        return new PromptAccepted(messageId, _time.GetUtcNow());
    }

    public async ValueTask ReplyPermissionAsync(string sessionId, string permissionId, PermissionReply reply, CancellationToken ct)
    {
        var decision = reply switch
        {
            PermissionReply.Once => "once",
            PermissionReply.Always => "always",
            _ => "reject",
        };

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [_routes.IsV2 ? "decision" : "response"] = decision,
        };

        await _api.PostJsonAsync(_routes.Permission(sessionId, permissionId), body, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ProviderEvent> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in _sse.StreamAsync(ct).ConfigureAwait(false))
        {
            if (frame.Reconnected)
            {
                _v2.Reset();
                yield return new ProviderEvent(ProviderEventKinds.SnapshotRequired, null);
                continue;
            }

            if (frame.Data is null)
            {
                continue;
            }

            if (!_routes.IsV2)
            {
                if (OpenCodeMapper.TryParseEvent(frame.Data, out var legacyEvent))
                {
                    yield return legacyEvent;
                }

                continue;
            }

            foreach (var translated in _v2.Translate(frame.Data))
            {
                yield return translated;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _sseClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<PromptAccepted> PromptV2Async(
        string sessionId,
        PromptRequest req,
        string messageId,
        CancellationToken ct)
    {
        if (req.Model is { } model)
        {
            var modelBody = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = ToModelRef(model),
            };
            await _api.PostJsonAsync(_routes.SessionModel(sessionId), modelBody, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(req.Agent))
        {
            var agentBody = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agent"] = req.Agent,
            };
            await _api.PostJsonAsync(_routes.SessionAgent(sessionId), agentBody, ct).ConfigureAwait(false);
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = req.Text,
        };

        if (messageId.Length > 0)
        {
            body["id"] = messageId;
        }

        var response = await _api.PostJsonAsync(_routes.PromptAsync(sessionId), body, ct).ConfigureAwait(false);
        var acceptedAt = _time.GetUtcNow();
        if (response is { ValueKind: JsonValueKind.Object } responseValue)
        {
            var user = UnwrapData(responseValue);
            if (user.ValueKind == JsonValueKind.Object)
            {
                var acceptedId = Str(user, "id");
                if (!string.IsNullOrEmpty(acceptedId))
                {
                    messageId = acceptedId;
                }

                if (user.TryGetProperty("time", out var time) &&
                    time.ValueKind == JsonValueKind.Object &&
                    time.TryGetProperty("created", out var created) &&
                    created.ValueKind == JsonValueKind.Number &&
                    created.TryGetInt64(out var createdMs))
                {
                    acceptedAt = DateTimeOffset.FromUnixTimeMilliseconds(createdMs);
                }
            }
        }

        return new PromptAccepted(messageId, acceptedAt);
    }

    private async ValueTask<IReadOnlyList<MessageView>> FetchV1MessagesAsync(string sessionId, CancellationToken ct)
    {
        var messages = await _api.GetJsonAsync(_routes.Messages(sessionId), ct).ConfigureAwait(false);
        return messages is { } messagesValue
            ? _mapper.ParseMessages(messagesValue)
            : Array.Empty<MessageView>();
    }

    private async ValueTask<IReadOnlyList<MessageView>> FetchV2MessagesAsync(
        string sessionId,
        int wanted,
        CancellationToken ct)
    {
        var messages = new List<MessageView>();
        string? cursor = null;
        for (var pageIndex = 0; pageIndex < V2MaxFetchPages && messages.Count < wanted; pageIndex++)
        {
            var path = _routes.MessagesPage(
                sessionId,
                cursor is null ? "asc" : null,
                V2FetchPageSize,
                cursor);
            var body = await _api.GetJsonAsync(path, ct).ConfigureAwait(false);
            if (body is not { } bodyValue)
            {
                break;
            }

            messages.AddRange(_mapper.ParseMessages(bodyValue));
            cursor = ReadNextCursor(bodyValue);
            if (cursor is null)
            {
                break;
            }
        }

        return messages;
    }

    private async ValueTask<JsonElement?> ReadStatusMapAsync(JsonElement sessions, CancellationToken ct)
    {
        JsonElement? raw = null;
        try
        {
            raw = await _api.GetJsonAsync(_routes.SessionStatus, ct).ConfigureAwait(false);
        }
        catch (OpenCodeHttpException)
        {
        }
        catch (HttpRequestException)
        {
        }

        if (raw is not { } statusRoot)
        {
            return null;
        }

        if (!_routes.IsV2)
        {
            return statusRoot;
        }

        var active = UnwrapData(statusRoot);
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        var list = UnwrapData(sessions);
        if (list.ValueKind != JsonValueKind.Array)
        {
            return raw;
        }

        foreach (var session in list.EnumerateArray())
        {
            if (Str(session, "id") is not { } sessionId)
            {
                continue;
            }

            var running = active.ValueKind == JsonValueKind.Object && active.TryGetProperty(sessionId, out _);
            map[sessionId] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = running ? "busy" : "idle",
            };
        }

        return JsonSerializer.SerializeToElement(map, JsonOptions);
    }

    private async ValueTask<JsonElement?> ReadSessionStatusAsync(string sessionId, CancellationToken ct)
    {
        JsonElement? statusMap;
        try
        {
            statusMap = await _api.GetJsonAsync(_routes.SessionStatus, ct).ConfigureAwait(false);
        }
        catch (OpenCodeHttpException)
        {
            return _routes.IsV2 ? IdleStatus : null;
        }
        catch (HttpRequestException)
        {
            return _routes.IsV2 ? IdleStatus : null;
        }

        if (statusMap is not { ValueKind: JsonValueKind.Object } map)
        {
            return _routes.IsV2 ? IdleStatus : null;
        }

        var normalized = _routes.IsV2 ? UnwrapData(map) : map;
        if (normalized.ValueKind == JsonValueKind.Object && normalized.TryGetProperty(sessionId, out var entry))
        {
            return _routes.IsV2 ? BusyStatus : entry;
        }

        return _routes.IsV2 ? IdleStatus : null;
    }

    private static JsonElement ToModelRef(ModelRef model) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = model.ModelId,
                ["providerID"] = model.ProviderId,
            },
            JsonOptions);

    private static string? ReadNextCursor(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty("cursor", out var cursor) &&
        cursor.ValueKind == JsonValueKind.Object &&
        cursor.TryGetProperty("next", out var next) &&
        next.ValueKind == JsonValueKind.String &&
        next.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static JsonElement UnwrapData(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("data", out var data)
            ? data
            : element;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeMessageId(string messageId) =>
        messageId.StartsWith("msg", StringComparison.Ordinal) ? messageId : "msg_" + messageId;

    private static int ParseCursor(string? cursor) =>
        int.TryParse(cursor, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset > 0
            ? offset
            : 0;
}
