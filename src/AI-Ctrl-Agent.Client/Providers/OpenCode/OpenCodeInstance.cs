using System.Net;
using System.Text.Json;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed class OpenCodeInstance : IProviderInstance
{
    private const int DefaultPageLimit = 50;

    private const int MaxPageLimit = 200;

    private readonly OpenCodeHttpApi _api;
    private readonly OpenCodeMapper _mapper;
    private readonly OpenCodeSseReader _sse;
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
        TimeProvider time)
    {
        _api = api;
        _mapper = mapper;
        _sse = sse;
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
            Managed: false,
            probe.Capabilities);
    }

    public InstanceInfo Info { get; }

    public CapabilitySet Capabilities => Info.Capabilities;

    public async ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionQuery q, CancellationToken ct)
    {
        var sessions = await _api.GetJsonAsync(_routes.Sessions, ct).ConfigureAwait(false);
        if (sessions is not { } sessionsValue)
        {
            return Array.Empty<SessionSummary>();
        }

        JsonElement? statusMap = null;
        try
        {
            statusMap = await _api.GetJsonAsync(_routes.SessionStatus, ct).ConfigureAwait(false);
        }
        catch (OpenCodeHttpException)
        {
        }
        catch (HttpRequestException)
        {
        }

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

        if (session is not { ValueKind: JsonValueKind.Object } sessionValue)
        {
            return null;
        }

        JsonElement? status = null;
        try
        {
            var statusMap = await _api.GetJsonAsync(_routes.SessionStatus, ct).ConfigureAwait(false);
            if (statusMap is { ValueKind: JsonValueKind.Object } map && map.TryGetProperty(sessionId, out var entry))
            {
                status = entry;
            }
        }
        catch (OpenCodeHttpException)
        {
        }
        catch (HttpRequestException)
        {
        }

        return _mapper.ParseSessionSummary(sessionId, sessionValue, status, _machine, Info.Id);
    }

    public async ValueTask<Page<MessageView>> GetMessagesAsync(string sessionId, PageRequest page, CancellationToken ct)
    {
        var messages = await _api.GetJsonAsync(_routes.Messages(sessionId), ct).ConfigureAwait(false);
        var all = messages is { } messagesValue
            ? _mapper.ParseMessages(messagesValue)
            : Array.Empty<MessageView>();

        var offset = ParseCursor(page.Cursor);
        var limit = page.Limit <= 0 ? DefaultPageLimit : Math.Min(page.Limit, MaxPageLimit);
        var slice = all.Skip(offset).Take(limit).ToList();
        var consumed = offset + slice.Count;
        var nextCursor = consumed < all.Count ? consumed.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
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

        var messageId = string.IsNullOrEmpty(req.ClientMessageId)
            ? string.Empty
            : NormalizeMessageId(req.ClientMessageId);

        if (messageId.Length > 0)
        {
            body["messageID"] = messageId;
        }

        await _api.PostJsonAsync(_routes.PromptAsync(sessionId), body, ct).ConfigureAwait(false);
        return new PromptAccepted(messageId, _time.GetUtcNow());
    }

    public async ValueTask ReplyPermissionAsync(string sessionId, string permissionId, PermissionReply reply, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["response"] = reply switch
            {
                PermissionReply.Once => "once",
                PermissionReply.Always => "always",
                _ => "reject",
            },
        };

        await _api.PostJsonAsync(_routes.Permission(sessionId, permissionId), body, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ProviderEvent> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in _sse.StreamAsync(ct).ConfigureAwait(false))
        {
            if (frame.Reconnected)
            {
                yield return new ProviderEvent(ProviderEventKinds.SnapshotRequired, null);
                continue;
            }

            if (frame.Data is not null && OpenCodeMapper.TryParseEvent(frame.Data, out var providerEvent))
            {
                yield return providerEvent;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _sseClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string NormalizeMessageId(string messageId) =>
        messageId.StartsWith("msg", StringComparison.Ordinal) ? messageId : "msg_" + messageId;

    private static readonly Dictionary<string, object?> EmptyBody = new(StringComparer.Ordinal);

    private static int ParseCursor(string? cursor) =>
        int.TryParse(cursor, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset > 0
            ? offset
            : 0;
}
