using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;

namespace AiCtrl.Server.Requests;

public sealed record RemoteQueryResult(
    bool Success,
    JsonElement? Data,
    ResponseMeta? Meta,
    string? ErrorCode,
    string? Detail,
    int StatusCode);

public sealed class RemoteQueryService
{
    private readonly RequestDispatchService _dispatch;

    private readonly ServerDb _db;

    private readonly ConnectionRegistry _connections;

    private readonly TimeProvider _time;

    private readonly ILogger<RemoteQueryService> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inflight = new(StringComparer.Ordinal);

    public RemoteQueryService(
        RequestDispatchService dispatch,
        ServerDb db,
        ConnectionRegistry connections,
        TimeProvider time,
        ILogger<RemoteQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatch = dispatch;
        _db = db;
        _connections = connections;
        _time = time;
        _logger = logger;
    }

    public async Task<RemoteQueryResult> QueryAsync(
        string machineId,
        string type,
        JsonElement payload,
        string cacheKey,
        string issuedBy,
        string traceId,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var machine = await _db.GetMachineAsync(machineId, ct).ConfigureAwait(false);
        if (machine is null || machine.RevokedAt is not null)
        {
            return new RemoteQueryResult(
                false,
                null,
                null,
                AcErrorCodes.MachineNotFound,
                $"Machine '{machineId}' was not found.",
                StatusCodes.Status404NotFound);
        }

        var key = machineId + "|" + cacheKey;
        var gate = _inflight.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        var waitStarted = _time.GetUtcNow();
        var contended = !await gate.WaitAsync(0, ct).ConfigureAwait(false);
        if (contended)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            if (contended)
            {
                var shared = await _db.GetQueryCacheAsync(machineId, cacheKey, ct).ConfigureAwait(false);
                if (shared is not null && shared.Value.AsOf >= waitStarted)
                {
                    using var sharedDocument = JsonDocument.Parse(shared.Value.PayloadJson);
                    var sharedElapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    return new RemoteQueryResult(
                        true,
                        sharedDocument.RootElement.Clone(),
                        new ResponseMeta(
                            MetaSources.Live,
                            shared.Value.AsOf,
                            MachineOnline: _connections.GetConnection(machineId) is not null,
                            Stale: false,
                            sharedElapsed,
                            traceId),
                        null,
                        null,
                        StatusCodes.Status200OK);
                }
            }

            var requestId = RequestIds.New();
            var result = await _dispatch.SendQueryAsync(
                    new MachineId(machineId),
                    requestId,
                    type,
                    payload,
                    traceId,
                    issuedBy,
                    ct)
                .ConfigureAwait(false);

            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result.Outcome == DispatchOutcome.Completed && result.Response is not null)
            {
                var response = result.Response;
                if (response.Status == ResponseStatus.Succeeded)
                {
                    var data = response.Data;
                    await _db.SetQueryCacheAsync(machineId, cacheKey, data?.GetRawText() ?? "null", response.AsOf, ct)
                        .ConfigureAwait(false);
                    _logger.LogInformation(
                        "Query {Type} for {MachineId} served live in {ElapsedMs}ms.",
                        type,
                        machineId,
                        elapsed);
                    return new RemoteQueryResult(
                        true,
                        data,
                        new ResponseMeta(
                            MetaSources.Live,
                            response.AsOf,
                            MachineOnline: true,
                            Stale: false,
                            elapsed,
                            traceId),
                        null,
                        null,
                        StatusCodes.Status200OK);
                }

                var validationStatus = response.ErrorCode switch
                {
                    AcErrorCodes.PathInvalid => StatusCodes.Status400BadRequest,
                    AcErrorCodes.PathDenied => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status502BadGateway,
                };
                return new RemoteQueryResult(
                    false,
                    null,
                    null,
                    validationStatus == StatusCodes.Status502BadGateway
                        ? AcErrorCodes.QueryFailed
                        : response.ErrorCode,
                    response.ErrorMessage ?? $"Query '{type}' failed.",
                    validationStatus);
            }

            var cached = await _db.GetQueryCacheAsync(machineId, cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                using var document = JsonDocument.Parse(cached.Value.PayloadJson);
                _logger.LogInformation(
                    "Query {Type} for {MachineId} served from cache after {Outcome}.",
                    type,
                    machineId,
                    result.Outcome);
                return new RemoteQueryResult(
                    true,
                    document.RootElement.Clone(),
                    new ResponseMeta(
                        MetaSources.Cache,
                        cached.Value.AsOf,
                        MachineOnline: _connections.GetConnection(machineId) is not null,
                        Stale: true,
                        elapsed,
                        traceId),
                    null,
                    null,
                    StatusCodes.Status200OK);
            }

            return result.Outcome == DispatchOutcome.Timeout
                ? new RemoteQueryResult(
                    false,
                    null,
                    null,
                    AcErrorCodes.QueryTimeout,
                    $"Query '{type}' timed out and no cache is available.",
                    StatusCodes.Status504GatewayTimeout)
                : new RemoteQueryResult(
                    false,
                    null,
                    null,
                    AcErrorCodes.MachineOffline,
                    $"Machine '{machineId}' is offline and no cache is available.",
                    StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            gate.Release();
        }
    }
}
