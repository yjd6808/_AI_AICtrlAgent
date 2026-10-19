using System.Text.Json;

namespace AiCtrl.Core.Commands;

public sealed record CommandResult(
    CommandStatus Status,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Status == CommandStatus.Succeeded;

    public static CommandResult Success(JsonElement? result) => new(CommandStatus.Succeeded, result, null, null);

    public static CommandResult Failure(string code, string message) => new(CommandStatus.Failed, null, code, message);

    public static CommandResult Unconfirmed(string code, string message) => new(CommandStatus.Unknown, null, code, message);
}

public sealed record CommandView(
    string CommandId,
    string Type,
    CommandStatus Status,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorMessage,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset UpdatedAt);

public sealed record AbortAndPromptResult(int AbortedSessions, string MessageId, DateTimeOffset AcceptedAt);
