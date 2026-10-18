using System.Text.Json;

namespace AiCtrl.Core.Protocol;

public enum RequestKind
{
    Query,
    Command,
}

public sealed record RequestPacket(
    string RequestId,
    RequestKind Kind,
    string Type,
    JsonElement Payload,
    string TraceId,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int Attempt);

public sealed record AckPacket(string RequestId, DateTimeOffset ReceivedAt);

public sealed record ProgressPacket(string RequestId, string Step, JsonElement? Data, DateTimeOffset At);

public enum ResponseStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Expired,
    Unknown,
}

public sealed record ResponsePacket(
    string RequestId,
    ResponseStatus Status,
    JsonElement? Data,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset AsOf,
    long ElapsedMs);

public sealed record Ack(DateTimeOffset At);
