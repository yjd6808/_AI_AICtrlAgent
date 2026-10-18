using System.Text.Json;
using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Commands;

public sealed record CommandEnvelope(
    string CommandId,
    MachineId Machine,
    string Type,
    JsonElement Payload,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int Attempt);

public enum CommandStatus
{
    Created,
    Queued,
    Dispatched,
    Acknowledged,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Expired,
    Unknown,
}
