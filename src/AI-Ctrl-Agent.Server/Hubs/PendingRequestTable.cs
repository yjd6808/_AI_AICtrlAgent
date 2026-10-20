using System.Collections.Concurrent;
using AiCtrl.Core.Protocol;

namespace AiCtrl.Server.Hubs;

public sealed class PendingRequest
{
    private readonly TaskCompletionSource<AckPacket> _acknowledged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<ResponsePacket> _responded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingRequest(RequestPacket packet, string machineId)
    {
        Packet = packet;
        MachineId = machineId;
    }

    public RequestPacket Packet { get; }

    public string MachineId { get; }

    public Task<AckPacket> Acknowledged => _acknowledged.Task;

    public Task<ResponsePacket> Responded => _responded.Task;

    public string? LastStep { get; private set; }

    public bool TryAcknowledge(AckPacket packet) => _acknowledged.TrySetResult(packet);

    public bool TryProgress(ProgressPacket packet)
    {
        LastStep = packet.Step;
        return true;
    }

    public bool TryRespond(ResponsePacket packet) => _responded.TrySetResult(packet);
}

public sealed class PendingRequestTable
{
    private readonly ConcurrentDictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);

    public PendingRequest Add(RequestPacket packet, string machineId)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var pending = new PendingRequest(packet, machineId);
        if (!_requests.TryAdd(packet.RequestId, pending))
        {
            throw new InvalidOperationException($"Request '{packet.RequestId}' is already pending.");
        }

        return pending;
    }

    public PendingRequest? Get(string requestId) => _requests.GetValueOrDefault(requestId);

    public PendingRequest? Remove(string requestId) =>
        _requests.TryRemove(requestId, out var pending) ? pending : null;

    public string? LastStep(string requestId) => Get(requestId)?.LastStep;
}
