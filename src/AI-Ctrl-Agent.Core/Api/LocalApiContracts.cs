using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Api;

public sealed record LocalStatusDto(
    MachineId MachineId,
    string Version,
    bool ServerConnected,
    int LocalApiPort,
    int InstanceCount,
    int SessionCount);

public sealed record SessionPageDto(
    IReadOnlyList<SessionSummary> Sessions,
    string? NextCursor,
    bool HasMore);

public sealed record MessagePageDto(
    IReadOnlyList<MessageView> Messages,
    string? NextCursor,
    bool HasMore);

public sealed record AbortSessionBody(bool IncludeChildren = true);

public sealed record SessionCreateBody(
    string? WorkspaceId,
    string? Title,
    string? InitialPrompt,
    ModelRef? Model,
    string? Directory = null);

public sealed record PromptBody(string Text, ModelRef? Model, string? Agent);

public sealed record AbortAndPromptBody(string Text, ModelRef? Model);

public sealed record PermissionReplyBody(string Response);
