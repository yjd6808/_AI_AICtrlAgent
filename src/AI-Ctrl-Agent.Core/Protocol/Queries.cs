using System.Text.Json;

namespace AiCtrl.Core.Protocol;

public sealed record QueryEnvelope(
    string QueryId,
    string Type,
    JsonElement Payload,
    string TraceId,
    int TimeoutMs);

public sealed record QueryResult(
    string QueryId,
    bool Ok,
    JsonElement? Data,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset AsOf,
    long ElapsedMs);
