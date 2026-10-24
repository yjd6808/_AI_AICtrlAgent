using System.Text;
using System.Text.Json;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Providers.OpenCode;

/// <summary>
/// Converts OpenCode v2 SSE events into the v1 event shape consumed by
/// <c>MonitorSystem</c> and <c>StreamSystem</c>.
/// </summary>
public sealed class OpenCodeV2EventTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly Dictionary<string, AssistantMessage> _assistants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolPart> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _partStarts = new(StringComparer.Ordinal);

    public OpenCodeV2EventTranslator(TimeProvider time)
    {
        _time = time;
    }

    public void Reset()
    {
        _assistants.Clear();
        _tools.Clear();
        _partStarts.Clear();
    }

    public IReadOnlyList<ProviderEvent> Translate(string json)
    {
        var events = new List<ProviderEvent>(2);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return events;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return events;
            }

            var type = Str(root, "type");
            if (type is null || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return events;
            }

            var created = Long(root, "created") ?? _time.GetUtcNow().ToUnixTimeMilliseconds();
            switch (type)
            {
                case "session.inbox.enqueued":
                    AddInboxEnqueued(events, data, created);
                    break;

                case "session.execution.started":
                    AddStatus(events, data, "busy");
                    break;

                case "session.execution.succeeded":
                case "session.execution.interrupted":
                    AddStatus(events, data, "idle");
                    break;

                case "session.execution.failed":
                    AddError(events, data);
                    AddStatus(events, data, "idle");
                    break;

                case "session.error":
                    AddError(events, data);
                    break;

                case "session.step.started":
                    AddStepStarted(events, data, created);
                    break;

                case "session.step.ended":
                    AddStepEnded(events, data, created);
                    break;

                case "session.text.started":
                    AddPartStart(events, data, created, "text");
                    break;

                case "session.reasoning.started":
                    AddPartStart(events, data, created, "reasoning");
                    break;

                case "session.text.delta":
                    AddPartDelta(events, data, "text");
                    break;

                case "session.reasoning.delta":
                    AddPartDelta(events, data, "reasoning");
                    break;

                case "session.text.ended":
                    AddPartEnded(events, data, created, "text");
                    break;

                case "session.reasoning.ended":
                    AddPartEnded(events, data, created, "reasoning");
                    break;

                case "session.tool.input.started":
                    AddToolStarted(events, data, created);
                    break;

                case "session.tool.input.ended":
                    AddToolInputEnded(events, data);
                    break;

                case "session.tool.called":
                    AddToolCalled(events, data, created);
                    break;

                case "session.tool.success":
                    AddToolCompleted(events, data, created, failed: false);
                    break;

                case "session.tool.failed":
                    AddToolCompleted(events, data, created, failed: true);
                    break;

                case "permission.asked":
                    AddPermissionAsked(events, data);
                    break;
            }
        }

        return events;
    }

    private static void AddInboxEnqueued(List<ProviderEvent> events, JsonElement data, long created)
    {
        var sessionId = Str(data, "sessionID");
        var inboxId = Str(data, "inboxID");
        if (sessionId is null || inboxId is null || Nested(data, "item") is not { } item)
        {
            return;
        }

        if (Str(item, "type") is not "user")
        {
            return;
        }

        var text = Nested(item, "payload") is { } payload ? Str(payload, "text") : null;
        var createdAt = Nested(data, "time") is { } time && Long(time, "created") is { } itemCreated
            ? itemCreated
            : created;
        var partId = inboxId + ":text:0";

        events.Add(MessageUpdated(sessionId, inboxId, "user", createdAt, null, null));
        if (text is not null)
        {
            events.Add(PartUpdated(
                sessionId,
                Obj(
                    ("id", partId),
                    ("messageID", inboxId),
                    ("type", "text"),
                    ("text", text),
                    ("time", Obj(("created", createdAt), ("end", createdAt))))));
        }
    }

    private static void AddStatus(List<ProviderEvent> events, JsonElement data, string status)
    {
        if (Str(data, "sessionID") is not { } sessionId)
        {
            return;
        }

        events.Add(Event(
            "session.status",
            Obj(("sessionID", sessionId), ("status", Obj(("type", status))))));
    }

    private static void AddError(List<ProviderEvent> events, JsonElement data)
    {
        if (Str(data, "sessionID") is not { } sessionId || Nested(data, "error") is not { } error)
        {
            return;
        }

        events.Add(Event("session.error", Obj(("sessionID", sessionId), ("error", error))));
    }

    private void AddStepStarted(List<ProviderEvent> events, JsonElement data, long created)
    {
        var sessionId = Str(data, "sessionID");
        var messageId = Str(data, "assistantMessageID");
        if (sessionId is null || messageId is null)
        {
            return;
        }

        var started = Long(data, "started") ?? created;
        var model = Nested(data, "model");
        var providerId = model is { } modelValue ? Str(modelValue, "providerID") : null;
        var modelId = model is { } modelIdValue ? Str(modelIdValue, "id") : null;
        _assistants[messageId] = new AssistantMessage(started, providerId, modelId);
        events.Add(MessageUpdated(sessionId, messageId, "assistant", started, null, model));
    }

    private void AddStepEnded(List<ProviderEvent> events, JsonElement data, long created)
    {
        var sessionId = Str(data, "sessionID");
        var messageId = Str(data, "assistantMessageID");
        if (sessionId is null || messageId is null)
        {
            return;
        }

        _assistants.TryGetValue(messageId, out var assistant);
        var started = assistant?.Created ?? created;
        var model = assistant is null || assistant.ProviderId is null || assistant.ModelId is null
            ? (JsonElement?)null
            : Obj(("id", assistant.ModelId), ("providerID", assistant.ProviderId));
        events.Add(MessageUpdated(sessionId, messageId, "assistant", started, created, model));
    }

    private void AddPartStart(List<ProviderEvent> events, JsonElement data, long created, string kind)
    {
        var sessionId = Str(data, "sessionID");
        var messageId = Str(data, "assistantMessageID");
        if (sessionId is null || messageId is null)
        {
            return;
        }

        var partId = PartId(messageId, kind, data);
        _partStarts[partId] = created;
        events.Add(PartUpdated(
            sessionId,
            Obj(
                ("id", partId),
                ("messageID", messageId),
                ("type", kind),
                ("text", string.Empty),
                ("time", Obj(("created", created))))));
    }

    private static void AddPartDelta(List<ProviderEvent> events, JsonElement data, string kind)
    {
        var sessionId = Str(data, "sessionID");
        var messageId = Str(data, "assistantMessageID");
        var delta = Str(data, "delta");
        if (sessionId is null || messageId is null || delta is null)
        {
            return;
        }

        events.Add(Event(
            "message.part.delta",
            Obj(
                ("sessionID", sessionId),
                ("partID", PartId(messageId, kind, data)),
                ("messageID", messageId),
                ("field", "text"),
                ("delta", delta))));
    }

    private void AddPartEnded(List<ProviderEvent> events, JsonElement data, long created, string kind)
    {
        var sessionId = Str(data, "sessionID");
        var messageId = Str(data, "assistantMessageID");
        if (sessionId is null || messageId is null)
        {
            return;
        }

        var partId = PartId(messageId, kind, data);
        var start = _partStarts.TryGetValue(partId, out var stored) ? stored : (long?)null;
        events.Add(PartUpdated(
            sessionId,
            Obj(
                ("id", partId),
                ("messageID", messageId),
                ("type", kind),
                ("text", Str(data, "text") ?? string.Empty),
                ("time", Obj(("created", start), ("end", created))))));
    }

    private void AddToolStarted(List<ProviderEvent> events, JsonElement data, long created)
    {
        var sessionId = Str(data, "sessionID");
        var messageId = Str(data, "assistantMessageID");
        var toolId = Str(data, "id");
        if (sessionId is null || messageId is null || toolId is null)
        {
            return;
        }

        var tool = new ToolPart
        {
            Id = toolId,
            MessageId = messageId,
            Name = Str(data, "name") ?? string.Empty,
            Started = created,
        };
        _tools[toolId] = tool;
        events.Add(ToolUpdated(sessionId, tool, "running", null, null));
    }

    private void AddToolInputEnded(List<ProviderEvent> events, JsonElement data)
    {
        var sessionId = Str(data, "sessionID");
        var toolId = Str(data, "id");
        if (sessionId is null || toolId is null || !_tools.TryGetValue(toolId, out var tool))
        {
            return;
        }

        tool.Input = Str(data, "text");
        events.Add(ToolUpdated(sessionId, tool, "running", null, null));
    }

    private void AddToolCalled(List<ProviderEvent> events, JsonElement data, long created)
    {
        var sessionId = Str(data, "sessionID");
        var toolId = Str(data, "id");
        if (sessionId is null || toolId is null)
        {
            return;
        }

        if (!_tools.TryGetValue(toolId, out var tool))
        {
            var messageId = Str(data, "assistantMessageID");
            if (messageId is null)
            {
                return;
            }

            tool = new ToolPart { Id = toolId, MessageId = messageId, Name = string.Empty, Started = created };
            _tools[toolId] = tool;
        }

        if (data.TryGetProperty("input", out var input))
        {
            tool.Input = input.ValueKind == JsonValueKind.String
                ? input.GetString()
                : JsonSerializer.Serialize(input, JsonOptions);
        }

        events.Add(ToolUpdated(sessionId, tool, "running", null, null));
    }

    private void AddToolCompleted(List<ProviderEvent> events, JsonElement data, long created, bool failed)
    {
        var sessionId = Str(data, "sessionID");
        var toolId = Str(data, "id");
        if (sessionId is null || toolId is null)
        {
            return;
        }

        if (!_tools.TryGetValue(toolId, out var tool))
        {
            var messageId = Str(data, "assistantMessageID");
            if (messageId is null)
            {
                return;
            }

            tool = new ToolPart { Id = toolId, MessageId = messageId, Name = string.Empty, Started = created };
            _tools[toolId] = tool;
        }

        var output = failed ? ReadError(data) : ReadContent(data);
        events.Add(ToolUpdated(sessionId, tool, failed ? "error" : "completed", created, output));
    }

    private static void AddPermissionAsked(List<ProviderEvent> events, JsonElement data)
    {
        var sessionId = Str(data, "sessionID");
        var permissionId = Str(data, "id");
        if (sessionId is null || permissionId is null)
        {
            return;
        }

        var patterns = new List<string>();
        if (data.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in resources.EnumerateArray())
            {
                if (resource.ValueKind == JsonValueKind.String && resource.GetString() is { } pattern)
                {
                    patterns.Add(pattern);
                }
            }
        }

        events.Add(Event(
            "permission.asked",
            Obj(
                ("sessionID", sessionId),
                ("id", permissionId),
                ("permission", Str(data, "action") ?? string.Empty),
                ("patterns", patterns))));
    }

    private static ProviderEvent MessageUpdated(
        string sessionId,
        string messageId,
        string role,
        long created,
        long? completed,
        JsonElement? model)
    {
        var info = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["role"] = role,
            ["time"] = Obj(("created", created), ("completed", completed)),
        };
        if (model is { } modelValue)
        {
            info["providerID"] = Str(modelValue, "providerID");
            info["modelID"] = Str(modelValue, "id");
        }

        return Event(
            "message.updated",
            Obj(("sessionID", sessionId), ("info", JsonSerializer.SerializeToElement(info, JsonOptions))));
    }

    private static ProviderEvent PartUpdated(string sessionId, JsonElement part) =>
        Event("message.part.updated", Obj(("sessionID", sessionId), ("part", part)));

    private static ProviderEvent ToolUpdated(
        string sessionId,
        ToolPart tool,
        string status,
        long? completed,
        string? output) =>
        PartUpdated(
            sessionId,
            Obj(
                ("id", tool.Id),
                ("messageID", tool.MessageId),
                ("type", "tool"),
                ("tool", tool.Name),
                ("state", Obj(
                    ("status", status),
                    ("title", tool.Name.Length > 0 ? tool.Name : null),
                    ("input", tool.Input ?? string.Empty),
                    ("output", output),
                    ("time", Obj(("start", tool.Started), ("end", completed)))))));

    private static ProviderEvent Event(string kind, JsonElement properties) =>
        new(kind, properties);

    private static string PartId(string messageId, string kind, JsonElement data)
    {
        var ordinal = Long(data, "ordinal") ?? 0;
        return messageId + ":" + kind + ":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadContent(JsonElement data)
    {
        if (!data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var entry in content.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                Str(entry, "type") == "text" &&
                Str(entry, "text") is { } text)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? ReadError(JsonElement data)
    {
        if (Nested(data, "error") is not { } error)
        {
            return null;
        }

        return Str(error, "message") ??
               (Nested(error, "data") is { } errorData ? Str(errorData, "message") : null) ??
               error.GetRawText();
    }

    private static JsonElement Obj(params (string Key, object? Value)[] entries)
    {
        var values = new Dictionary<string, object?>(entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (value is not null)
            {
                values[key] = value;
            }
        }

        return JsonSerializer.SerializeToElement(values, JsonOptions);
    }

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

    private static JsonElement? Nested(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private sealed record AssistantMessage(long Created, string? ProviderId, string? ModelId);

    private sealed class ToolPart
    {
        public string Id { get; init; } = string.Empty;

        public string MessageId { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public long Started { get; init; }

        public string? Input { get; set; }
    }
}
