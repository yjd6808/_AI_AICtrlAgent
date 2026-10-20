using System.Text.Json;
using AiCtrl.Core.Commands;

namespace AiCtrl.Core.Api;

public sealed record CommandStepView(
    int Seq,
    string Step,
    string? Message,
    DateTimeOffset At);

public sealed record CommandDetailView(
    string CommandId,
    string Type,
    CommandStatus Status,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorMessage,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CommandStepView> Steps);

public sealed record CommandPendingView(
    string RequestId,
    string Status,
    string? LastStep);

public sealed record AuditLogView(
    long Id,
    DateTimeOffset At,
    string Actor,
    string? Ip,
    string? Country,
    string Action,
    string? Target,
    string? CommandId,
    string Result);

public sealed record LogFileView(
    string Name,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

public sealed record LogTextView(
    string Name,
    string Content,
    bool Truncated);
