using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;

namespace AiCtrl.Core.Protocol;

public interface IAgentHub
{
    Task<HelloAck> Hello(HelloRequest req);

    Task Ack(AckPacket p);

    Task Progress(ProgressPacket p);

    Task Respond(ResponsePacket p);

    Task<EventsAck> PushEvents(EventBatch batch);

    Task<Ack> PushSnapshot(MachineSnapshot snapshot);

    Task PushStream(StreamBatch batch);
}

public interface IAgentClient
{
    Task Request(RequestPacket p);

    Task Cancel(string requestId);

    Task RequestSnapshot(string reason);

    Task SetWatchedSessions(IReadOnlyList<SessionRef> sessions, int leaseSeconds);
}
