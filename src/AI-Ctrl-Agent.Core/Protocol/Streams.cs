using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Protocol;

public enum StreamOp
{
    Append,
    Replace,
    ToolState,
    PartDone,
}

public sealed record StreamChunk(
    string MessageId,
    string PartId,
    string PartType,
    long Seq,
    StreamOp Op,
    string? Text,
    ToolPartView? Tool);

public sealed record StreamBatch(
    MachineId Machine,
    string SessionId,
    long Epoch,
    IReadOnlyList<StreamChunk> Chunks);
