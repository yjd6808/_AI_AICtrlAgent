namespace AiCtrl.Core.Domain;

public sealed record RetryInfo(int Attempt, string? Message, DateTimeOffset? NextAt);

public sealed record ModelRef(string ProviderId, string ModelId);

public sealed record SessionSummary(
    SessionRef Ref,
    string Title,
    string? ParentId,
    string Directory,
    AcRunState RunState,
    ErrorClass LastError,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt,
    ModelRef? LastModel,
    RetryInfo? Retry,
    SessionDisplayState DisplayState,
    DateTimeOffset? BusySince,
    DateTimeOffset? LastActivityAt,
    int? MessageCount,
    bool ActiveOnInstance);

public sealed record MessageView(
    string MessageId,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ModelRef? Model,
    IReadOnlyList<PartView> Parts,
    ErrorInfo? Error);

public sealed record PartView(
    string PartId,
    string Type,
    string? Text,
    ToolPartView? Tool);

public sealed record ToolPartView(
    string Tool,
    string Status,
    string? Title,
    string? OutputPreview,
    string? Input,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record ErrorInfo(
    ErrorClass Class,
    string Name,
    string Message,
    int? StatusCode,
    string? RawJson);
