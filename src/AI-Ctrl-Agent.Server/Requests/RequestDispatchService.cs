using System.Text.Json;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AiCtrl.Server.Requests;

public enum DispatchOutcome
{
    Completed,
    Running,
    Offline,
    NoAck,
    Timeout,
}

public sealed record DispatchResult(
    DispatchOutcome Outcome,
    ResponsePacket? Response,
    string? LastStep,
    bool MachineOnline);

public sealed class RequestDispatchService
{
    private readonly IHubContext<AgentHub, IAgentClient> _hub;

    private readonly ConnectionRegistry _connections;

    private readonly PendingRequestTable _pending;

    private readonly RequestOptions _options;

    private readonly TimeProvider _time;

    private readonly ILogger<RequestDispatchService> _logger;

    public RequestDispatchService(
        IHubContext<AgentHub, IAgentClient> hub,
        ConnectionRegistry connections,
        PendingRequestTable pending,
        RequestOptions options,
        TimeProvider time,
        ILogger<RequestDispatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _hub = hub;
        _connections = connections;
        _pending = pending;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<DispatchResult> SendQueryAsync(
        MachineId machineId,
        string requestId,
        string type,
        JsonElement payload,
        string traceId,
        string issuedBy,
        CancellationToken ct)
    {
        var connectionId = _connections.GetConnection(machineId.Value);
        if (connectionId is null)
        {
            return new DispatchResult(DispatchOutcome.Offline, null, null, MachineOnline: false);
        }

        var now = _time.GetUtcNow();
        var packet = new RequestPacket(
            requestId,
            RequestKind.Query,
            type,
            payload,
            traceId,
            issuedBy,
            now,
            now.Add(_options.QueryTimeout),
            Attempt: 0);
        var pending = _pending.Add(packet, machineId.Value);
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["RequestId"] = requestId,
            ["MachineId"] = machineId.Value,
            ["Type"] = type,
        });

        try
        {
            await _hub.Clients.Client(connectionId).Request(packet).ConfigureAwait(false);
            var response = await pending.Responded.WaitAsync(_options.QueryTimeout, ct).ConfigureAwait(false);
            _logger.LogInformation("Query {Type} completed for {MachineId}.", type, machineId.Value);
            return new DispatchResult(DispatchOutcome.Completed, response, null, MachineOnline: true);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Query {Type} timed out for {MachineId}.", type, machineId.Value);
            return new DispatchResult(DispatchOutcome.Timeout, null, null, MachineOnline: true);
        }
        finally
        {
            _pending.Remove(requestId);
        }
    }

    public async Task<DispatchResult> SendCommandAsync(
        MachineId machineId,
        string requestId,
        string type,
        JsonElement payload,
        string traceId,
        string issuedBy,
        TimeSpan? responseTimeout,
        CancellationToken ct)
    {
        var effectiveTimeout = responseTimeout ?? _options.CommandTimeout;
        var connectionId = await _connections
            .WaitForConnectionAsync(machineId.Value, _options.OfflineWait, ct)
            .ConfigureAwait(false);
        if (connectionId is null)
        {
            return new DispatchResult(DispatchOutcome.Offline, null, null, MachineOnline: false);
        }

        var now = _time.GetUtcNow();
        var packet = new RequestPacket(
            requestId,
            RequestKind.Command,
            type,
            payload,
            traceId,
            issuedBy,
            now,
            now.Add(effectiveTimeout),
            Attempt: 0);
        var pending = _pending.Add(packet, machineId.Value);
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["RequestId"] = requestId,
            ["MachineId"] = machineId.Value,
            ["Type"] = type,
        });

        try
        {
            await _hub.Clients.Client(connectionId).Request(packet).ConfigureAwait(false);
            var acknowledged = false;
            for (var attempt = 0; attempt < _options.MaxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    packet = packet with { Attempt = attempt, ExpiresAt = _time.GetUtcNow().Add(effectiveTimeout) };
                    await _hub.Clients.Client(connectionId).Request(packet).ConfigureAwait(false);
                }

                try
                {
                    await pending.Acknowledged.WaitAsync(_options.AckTimeout, ct).ConfigureAwait(false);
                    acknowledged = true;
                    break;
                }
                catch (TimeoutException) when (attempt < _options.MaxAttempts - 1)
                {
                    _logger.LogWarning(
                        "Command {RequestId} was not acknowledged; retrying attempt {Attempt}.",
                        requestId,
                        attempt + 1);
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            if (!acknowledged)
            {
                _logger.LogWarning("Command {RequestId} was not acknowledged.", requestId);
                return new DispatchResult(DispatchOutcome.NoAck, null, null, MachineOnline: true);
            }

            var response = await pending.Responded.WaitAsync(effectiveTimeout, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Command {Type} {RequestId} completed with {Status}.",
                type,
                requestId,
                response.Status);
            return new DispatchResult(DispatchOutcome.Completed, response, pending.LastStep, MachineOnline: true);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Command {RequestId} did not finish before the HTTP wait deadline.", requestId);
            return new DispatchResult(
                DispatchOutcome.Running,
                null,
                pending.LastStep,
                MachineOnline: _connections.GetConnection(machineId.Value) is not null);
        }
        finally
        {
            _pending.Remove(requestId);
        }
    }
}
