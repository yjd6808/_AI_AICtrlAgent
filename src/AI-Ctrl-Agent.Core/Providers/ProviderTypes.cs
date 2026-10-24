using System.Text.Json;
using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Providers;

public static class SessionListViews
{
    public const string Active = "active";

    public const string All = "all";
}

public static class ProviderEventKinds
{
    public const string SnapshotRequired = "snapshot_required";
}

public sealed record SessionQuery(InstanceId Instance, string View, bool IncludeChildren, string? Cursor);

public sealed record SessionCreateRequest(string Directory, string? Title, ModelRef? Model);

public sealed record PageRequest(string? Cursor, int Limit);

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

public sealed record ModelOption(string ProviderId, string ModelId, string? Name);

public sealed record AbortResult(bool Aborted, string? Message);

public sealed record PromptAccepted(string MessageId, DateTimeOffset AcceptedAt);

public sealed record SessionCreateResult(SessionSummary Session, PromptAccepted? Prompt, string? PromptError);

public enum PermissionReply
{
    Once,
    Always,
    Reject,
}

public sealed record ProviderEvent(string Kind, JsonElement? Data);

public sealed record PromptRequest(string Text, ModelRef? Model, string? Agent, string? ClientMessageId);
