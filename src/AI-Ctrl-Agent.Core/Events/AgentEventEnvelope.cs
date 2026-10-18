using System.Text.Json;
using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Events;

public sealed record AgentEventEnvelope(
    long Seq,
    string Type,
    DateTimeOffset OccurredAt,
    JsonElement Payload);

public sealed record EventBatch(MachineId MachineId, IReadOnlyList<AgentEventEnvelope> Events);

public sealed record EventsAck(long AckedUpTo);
