using System.Text;
using System.Text.Json;
using AiCtrl.Client.Diagnostics;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed class OpenCodeMapper
{
    private readonly ErrorClassifier _classifier;

    public OpenCodeMapper(ErrorClassifier classifier)
    {
        _classifier = classifier;
    }

    public IReadOnlyList<SessionSummary> ParseSessionSummaries(JsonElement sessions, JsonElement? statusMap, MachineId machine, InstanceId instance)
    {
        var result = new List<SessionSummary>();
        var list = UnwrapData(sessions);
        if (list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var normalizedStatus = statusMap is { } statusRoot ? UnwrapData(statusRoot) : (JsonElement?)null;
        foreach (var session in list.EnumerateArray())
        {
            var id = Str(session, "id");
            if (id is null)
            {
                continue;
            }

            JsonElement? status = null;
            if (normalizedStatus is { ValueKind: JsonValueKind.Object } map &&
                map.TryGetProperty(id, out var entry))
            {
                status = entry;
            }

            result.Add(ParseSessionSummary(id, session, status, machine, instance));
        }

        return result;
    }

    public SessionSummary ParseSessionSummary(string id, JsonElement session, JsonElement? status, MachineId machine, InstanceId instance)
    {
        var title = Str(session, "title") ?? id;
        var directory = Str(session, "directory") ??
                        (Nested(session, "location") is { } location ? Str(location, "directory") : null) ??
                        string.Empty;
        var parentId = Str(session, "parentID");
        var updatedAt = FromMs(Long(session, "time", "updated") ?? Long(session, "time", "created"));
        var lastModel = ParseModelRef(Nested(session, "model"));

        var runState = AcRunState.Unknown;
        RetryInfo? retry = null;
        ErrorClass lastError = ErrorClass.None;
        string? lastErrorMessage = null;
        if (status is { } statusValue)
        {
            (runState, retry) = ParseStatus(statusValue);
            if (retry is not null)
            {
                var classified = _classifier.ClassifyRetry(RetryReason(statusValue), retry.Message);
                lastError = classified.Class;
                lastErrorMessage = retry.Message;
            }
        }

        return new SessionSummary(
            new SessionRef(machine, instance, id),
            title,
            parentId,
            directory,
            runState,
            lastError,
            lastErrorMessage,
            updatedAt,
            lastModel,
            retry,
            ToDisplayState(runState),
            BusySince: null,
            LastActivityAt: updatedAt,
            MessageCount: null,
            ActiveOnInstance: status is not null);
    }

    public (AcRunState State, RetryInfo? Retry) ParseStatus(JsonElement status)
    {
        return Str(status, "type") switch
        {
            "idle" => (AcRunState.Idle, null),
            "busy" or "running" => (AcRunState.Busy, null),
            "retry" => (AcRunState.Retrying, ParseRetry(status)),
            _ => (AcRunState.Unknown, null),
        };
    }

    public RetryInfo? ParseRetry(JsonElement status)
    {
        if (Str(status, "type") != "retry")
        {
            return null;
        }

        var attempt = (int)(Long(status, "attempt") ?? 0);
        var message = Str(status, "message");
        var nextAt = Long(status, "next") is { } next ? DateTimeOffset.FromUnixTimeMilliseconds(next) : (DateTimeOffset?)null;
        return new RetryInfo(attempt, message, nextAt);
    }

    public string? RetryReason(JsonElement status) =>
        Nested(status, "action") is { } action ? Str(action, "reason") : null;

    public IReadOnlyList<MessageView> ParseMessages(JsonElement messages)
    {
        var result = new List<MessageView>();
        var list = UnwrapData(messages);
        if (list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (entry.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                result.Add(ParseMessage(info, entry));
                continue;
            }

            var type = Str(entry, "type");
            if (type is "user" or "assistant")
            {
                result.Add(ParseV2Message(entry, type));
            }
        }

        return result;
    }

    public MessageView ParseMessage(JsonElement info, JsonElement entry)
    {
        var id = Str(info, "id") ?? string.Empty;
        var role = Str(info, "role") ?? string.Empty;
        var createdAt = FromMs(Long(info, "time", "created"));
        var completedAt = Long(info, "time", "completed") is { } completed ? DateTimeOffset.FromUnixTimeMilliseconds(completed) : (DateTimeOffset?)null;

        ModelRef? model = null;
        var modelId = Str(info, "modelID");
        var providerId = Str(info, "providerID");
        if (modelId is not null && providerId is not null)
        {
            model = new ModelRef(providerId, modelId);
        }
        else
        {
            model = ParseModelRef(Nested(info, "model"));
        }

        var parts = new List<PartView>();
        if (entry.TryGetProperty("parts", out var partsElement) && partsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in partsElement.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object)
                {
                    parts.Add(ParsePart(part));
                }
            }
        }

        ErrorInfo? error = null;
        if (info.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            error = ParseError(errorElement, errorElement.GetRawText());
        }

        return new MessageView(id, role, createdAt, completedAt, model, parts, error);
    }

    public PartView ParsePart(JsonElement part)
    {
        var id = Str(part, "id") ?? string.Empty;
        var type = Str(part, "type") ?? "unknown";
        var text = Str(part, "text");

        ToolPartView? tool = null;
        if (type == "tool")
        {
            var state = Nested(part, "state");
            tool = new ToolPartView(
                Str(part, "tool") ?? string.Empty,
                state is { } stateValue ? Str(stateValue, "status") ?? "unknown" : "unknown",
                state is { } titleState ? Str(titleState, "title") : null,
                state is { } outputState ? Preview(Str(outputState, "output"), 200) : null,
                state is { } inputState ? Str(inputState, "input") : null,
                state is { } timeState ? FromMs(Long(timeState, "time", "start")) : null,
                state is { } endState ? FromMs(Long(endState, "time", "end")) : null);
        }

        return new PartView(id, type, text, tool);
    }

    public ErrorInfo? ParseError(JsonElement error, string? rawJson = null)
    {
        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = Str(error, "name");
        var data = Nested(error, "data");
        var message = data is { } dataValue ? Str(dataValue, "message") : null;
        int? statusCode = data is { } codeValue && Long(codeValue, "statusCode") is { } code ? (int)code : null;
        return _classifier.Classify(name, message, statusCode, rawJson);
    }

    public IReadOnlyList<ModelOption> ParseModels(JsonElement body)
    {
        var result = new List<ModelOption>();

        var models = UnwrapData(body);
        if (models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var modelId = Str(model, "modelID") ?? Str(model, "id");
                var providerId = Str(model, "providerID");
                if (modelId is null || providerId is null)
                {
                    continue;
                }

                result.Add(new ModelOption(providerId, modelId, Str(model, "name")));
            }

            return result;
        }

        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("providers", out var providers) ||
            providers.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var provider in providers.EnumerateArray())
        {
            if (provider.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var providerId = Str(provider, "id") ?? string.Empty;
            if (!provider.TryGetProperty("models", out var providerModels) || providerModels.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var model in providerModels.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var modelId = Str(model, "id");
                if (modelId is not null)
                {
                    result.Add(new ModelOption(providerId, modelId, Str(model, "name")));
                }
            }
        }

        return result;
    }

    public static bool TryParseEvent(string json, out ProviderEvent providerEvent)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = Str(root, "type") ?? "unknown";
            JsonElement? properties = null;
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                properties = props.Clone();
            }

            providerEvent = new ProviderEvent(type, properties);
            return true;
        }
        catch (JsonException)
        {
            providerEvent = new ProviderEvent("unknown", null);
            return false;
        }
    }

    private MessageView ParseV2Message(JsonElement message, string type)
    {
        var id = Str(message, "id") ?? string.Empty;
        var createdAt = FromMs(Long(message, "time", "created"));
        var completedAt = Long(message, "time", "completed") is { } completed
            ? DateTimeOffset.FromUnixTimeMilliseconds(completed)
            : (DateTimeOffset?)null;

        if (type == "user")
        {
            var text = Str(message, "text") ?? string.Empty;
            var userParts = new List<PartView>
            {
                new(id + ":text:0", "text", text, null),
            };
            return new MessageView(id, "user", createdAt, completedAt, null, userParts, null);
        }

        var model = ParseModelRef(Nested(message, "model"));
        var parts = new List<PartView>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var entry in content.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                switch (Str(entry, "type"))
                {
                    case "text":
                        parts.Add(new PartView(id + ":text:" + index, "text", Str(entry, "text"), null));
                        break;
                    case "reasoning":
                        parts.Add(new PartView(id + ":reasoning:" + index, "reasoning", Str(entry, "text"), null));
                        break;
                    case "tool":
                        parts.Add(new PartView(
                            Str(entry, "id") ?? id + ":tool:" + index,
                            "tool",
                            null,
                            ParseV2Tool(entry)));
                        break;
                }

                index++;
            }
        }

        ErrorInfo? error = null;
        if (message.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            error = ParseError(errorElement, errorElement.GetRawText());
        }

        return new MessageView(id, "assistant", createdAt, completedAt, model, parts, error);
    }

    private static ToolPartView ParseV2Tool(JsonElement tool)
    {
        var state = Nested(tool, "state");
        var status = state is { } stateValue ? Str(stateValue, "status") ?? "unknown" : "unknown";
        var input = state is { } inputState ? ReadToolInput(inputState) : null;
        var output = state is { } outputState ? ReadToolOutput(outputState) : null;
        var time = Nested(tool, "time");
        var startedAt = time is { } startTime
            ? FromMsOrNull(Long(startTime, "ran") ?? Long(startTime, "created"))
            : null;
        var completedAt = time is { } endTime ? FromMsOrNull(Long(endTime, "completed")) : null;
        var title = state is { } titleState && Nested(titleState, "metadata") is { } metadata
            ? Str(metadata, "title")
            : null;

        return new ToolPartView(
            Str(tool, "name") ?? string.Empty,
            status,
            title,
            Preview(output, 200),
            input,
            startedAt,
            completedAt);
    }

    private static string? ReadToolInput(JsonElement state)
    {
        if (!state.TryGetProperty("input", out var input))
        {
            return null;
        }

        return input.ValueKind switch
        {
            JsonValueKind.String => input.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(input),
            _ => null,
        };
    }

    private static string? ReadToolOutput(JsonElement state)
    {
        if (state.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
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

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        if (Nested(state, "error") is { } error)
        {
            return Str(error, "message") ??
                   (Nested(error, "data") is { } data ? Str(data, "message") : null);
        }

        return null;
    }

    private static ModelRef? ParseModelRef(JsonElement? model)
    {
        if (model is not { ValueKind: JsonValueKind.Object } modelValue)
        {
            return null;
        }

        var id = Str(modelValue, "id") ?? Str(modelValue, "modelID");
        var providerId = Str(modelValue, "providerID");
        return id is not null && providerId is not null ? new ModelRef(providerId, id) : null;
    }

    private static SessionDisplayState ToDisplayState(AcRunState state) => state switch
    {
        AcRunState.Idle => SessionDisplayState.Idle,
        AcRunState.Busy => SessionDisplayState.Running,
        AcRunState.Retrying => SessionDisplayState.Retrying,
        AcRunState.WaitingPermission => SessionDisplayState.WaitingPermission,
        AcRunState.Errored => SessionDisplayState.Errored,
        AcRunState.ForeignBusy => SessionDisplayState.ForeignBusy,
        _ => SessionDisplayState.Unknown,
    };

    private static JsonElement UnwrapData(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("data", out var data)
            ? data
            : element;

    private static DateTimeOffset FromMs(long? ms) =>
        ms is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? FromMsOrNull(long? ms) =>
        ms is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;

    private static string? Preview(string? text, int length) =>
        text is null ? null : text.Length <= length ? text : text[..length];

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

    private static long? Long(JsonElement element, string parent, string name) =>
        Nested(element, parent) is { } nested ? Long(nested, name) : null;

    private static JsonElement? Nested(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;
}
