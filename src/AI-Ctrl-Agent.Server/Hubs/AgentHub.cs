using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AiCtrl.Server.Hubs;

[Authorize(AuthenticationSchemes = AgentTokenDefaults.Scheme)]
public sealed class AgentHub : Hub<IAgentClient>, IAgentHub
{
    private readonly ConnectionRegistry _connections;

    private readonly ServerDb _db;

    private readonly PendingRequestTable _pending;

    private readonly TimeProvider _time;

    private readonly ILogger<AgentHub> _logger;

    private readonly IHubContext<AgentHub> _hub;

    private readonly SessionStreamHub _streams;

    public AgentHub(
        ConnectionRegistry connections,
        ServerDb db,
        PendingRequestTable pending,
        TimeProvider time,
        ILogger<AgentHub> logger,
        IHubContext<AgentHub> hub,
        SessionStreamHub streams)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(streams);
        _connections = connections;
        _db = db;
        _pending = pending;
        _time = time;
        _logger = logger;
        _hub = hub;
        _streams = streams;
    }

    public async Task<HelloAck> Hello(HelloRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var machineId = MachineId();
        if (!string.Equals(machineId, req.MachineId.Value, StringComparison.Ordinal))
        {
            throw new HubException(AcErrorCodes.Unauthorized);
        }

        if (req.ProtocolVersion != ProtocolVersions.Current)
        {
            throw new HubException(AcErrorCodes.UnsupportedProtocol);
        }

        var http = Context.GetHttpContext();
        var ip = http?.Items[GeoGateMiddleware.IpItemKey] as string ?? http?.Connection.RemoteIpAddress?.ToString();
        var country = http?.Items[GeoGateMiddleware.CountryItemKey] as string;
        var record = await _db.RecordHelloAsync(machineId, req.BootId, req.LastAckedEventSeq, ip, country, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (!record.Found)
        {
            throw new HubException(AcErrorCodes.MachineNotFound);
        }

        if (record.Revoked)
        {
            throw new HubException(AcErrorCodes.MachineRevoked);
        }

        var previous = _connections.Register(machineId, Context.ConnectionId);
        if (previous is not null && !string.Equals(previous, Context.ConnectionId, StringComparison.Ordinal))
        {
            await _hub.Clients.Client(previous).SendAsync("ForceDisconnect", AcErrorCodes.MachineReplaced).ConfigureAwait(false);
        }

        _logger.LogInformation("Agent {MachineId} connected ({ConnectionId}).", machineId, Context.ConnectionId);
        return new HelloAck(
            _time.GetUtcNow().ToUnixTimeMilliseconds(),
            ip ?? string.Empty,
            country,
            Array.Empty<CommandEnvelope>(),
            record.SnapshotRequired);
    }

    public async Task Ack(AckPacket p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var machineId = MachineId();
        var pending = OwnedRequest(p.RequestId);
        pending?.TryAcknowledge(p);
        await _db.MarkCommandAcknowledgedAsync(p.RequestId, machineId, p.ReceivedAt, Context.ConnectionAborted)
            .ConfigureAwait(false);
        _logger.LogDebug("Command {RequestId} acknowledged by agent.", p.RequestId);
    }

    public async Task Progress(ProgressPacket p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var machineId = MachineId();
        var pending = OwnedRequest(p.RequestId);
        pending?.TryProgress(p);
        await _db.AddCommandStepAsync(
                p.RequestId,
                machineId,
                p.Step,
                p.Data?.GetRawText(),
                p.At,
                Context.ConnectionAborted)
            .ConfigureAwait(false);
        _logger.LogInformation("Command {RequestId} progress: {Step}.", p.RequestId, p.Step);
    }

    public async Task Respond(ResponsePacket p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var machineId = MachineId();
        var pending = OwnedRequest(p.RequestId);
        try
        {
            await _db.CompleteCommandAsync(p, machineId, Context.ConnectionAborted).ConfigureAwait(false);
        }
        finally
        {
            pending?.TryRespond(p);
        }

        _logger.LogInformation(
            "Request {RequestId} completed by agent with {Status}.",
            p.RequestId,
            p.Status);
    }

    public async Task<EventsAck> PushEvents(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!string.Equals(MachineId(), batch.MachineId.Value, StringComparison.Ordinal))
        {
            throw new HubException(AcErrorCodes.Unauthorized);
        }

        var applied = await _db.ApplyEventsAsync(batch.MachineId.Value, batch.Events, Context.ConnectionAborted).ConfigureAwait(false);
        if (applied < 0)
        {
            throw new HubException(AcErrorCodes.MachineNotFound);
        }

        return new EventsAck(applied);
    }

    public Task<Ack> PushSnapshot(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(MachineId(), snapshot.Machine.Id.Value, StringComparison.Ordinal))
        {
            throw new HubException(AcErrorCodes.Unauthorized);
        }

        return Task.FromResult(new Ack(_time.GetUtcNow()));
    }

    public async Task PushStream(StreamBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var machineId = MachineId();
        if (!string.Equals(machineId, batch.Machine.Value, StringComparison.Ordinal))
        {
            throw new HubException(AcErrorCodes.Unauthorized);
        }

        await _streams.PublishAsync(batch, Context.ConnectionAborted).ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var machineId = _connections.Remove(Context.ConnectionId);
        if (machineId is not null)
        {
            _logger.LogInformation("Agent {MachineId} disconnected ({ConnectionId}).", machineId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private PendingRequest? OwnedRequest(string requestId)
    {
        var pending = _pending.Get(requestId);
        if (pending is null)
        {
            return null;
        }

        if (!string.Equals(pending.MachineId, MachineId(), StringComparison.Ordinal))
        {
            throw new HubException(AcErrorCodes.Unauthorized);
        }

        return pending;
    }

    private string MachineId()
    {
        var claim = Context.User?.FindFirst(AgentTokenDefaults.MachineIdClaim)?.Value;
        if (string.IsNullOrEmpty(claim))
        {
            throw new HubException(AcErrorCodes.Unauthorized);
        }

        return claim;
    }
}
