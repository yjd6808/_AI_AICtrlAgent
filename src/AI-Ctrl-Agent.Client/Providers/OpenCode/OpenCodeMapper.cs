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
        if (sessions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var session in sessions.EnumerateArray())
        {
            var id = Str(session, "id");
            if (id is null)
            {
                continue;
            }

            JsonElement? status = null;
            if (statusMap is { ValueKind: JsonValueKind.Object } map && map.TryGetProperty(id, out var entry))
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
        var directory = Str(session, "directory") ?? string.Empty;
        var parentId = Str(session, "parentID");
        var updatedAt = FromMs(Long(session, "time", "updated"));
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
            "busy" => (AcRunState.Busy, null),
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
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in messages.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                result.Add(ParseMessage(info, entry));
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
            if (!provider.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var model in models.EnumerateArray())
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

    private static DateTimeOffset FromMs(long? ms) =>
        ms is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.UnixEpoch;

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
