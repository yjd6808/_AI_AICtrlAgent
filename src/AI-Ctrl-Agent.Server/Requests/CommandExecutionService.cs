using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;

namespace AiCtrl.Server.Requests;

public sealed record CommandExecutionResult(
    int StatusCode,
    CommandDetailView? Command,
    CommandPendingView? Pending,
    string? ErrorCode,
    string? Detail);

public sealed class CommandExecutionService
{
    private readonly RequestDispatchService _dispatch;

    private readonly ServerDb _db;

    private readonly TimeProvider _time;

    private readonly ILogger<CommandExecutionService> _logger;

    private readonly RequestOptions _options;

    public CommandExecutionService(
        RequestDispatchService dispatch,
        ServerDb db,
        TimeProvider time,
        RequestOptions options,
        ILogger<CommandExecutionService> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatch = dispatch;
        _db = db;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        string machineId,
        string type,
        JsonElement payload,
        string userId,
        string actor,
        string? idempotencyKey,
        string? requestedId,
        string traceId,
        string? ip,
        string? country,
        TimeSpan? wait,
        CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct).ConfigureAwait(false);
        if (machine is null || machine.RevokedAt is not null)
        {
            return new CommandExecutionResult(
                StatusCodes.Status404NotFound,
                null,
                null,
                AcErrorCodes.MachineNotFound,
                $"Machine '{machineId}' was not found.");
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existingId = await _db.GetIdempotentCommandIdAsync(userId, idempotencyKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                return await ExistingAsync(existingId, ct).ConfigureAwait(false);
            }
        }

        var commandId = string.IsNullOrWhiteSpace(requestedId) ? RequestIds.New() : requestedId;
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            var existing = await _db.GetCommandAsync(commandId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return Existing(existing);
            }
        }

        var now = _time.GetUtcNow();
        var envelope = new CommandEnvelope(
            commandId,
            new MachineId(machineId),
            type,
            payload,
            actor,
            now,
            now.AddMinutes(5),
            Attempt: 0);
        await _db.CreateCommandAsync(envelope, traceId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _db.SaveIdempotencyKeyAsync(userId, idempotencyKey, commandId, now, ct).ConfigureAwait(false);
        }

        await _db.AddAuditAsync(
                new AuditLogEntry(actor, ip, country, "command.issue", machineId, commandId, "accepted"),
                ct)
            .ConfigureAwait(false);

        var result = await _dispatch.SendCommandAsync(
                new MachineId(machineId),
                commandId,
                type,
                payload,
                traceId,
                actor,
                wait ?? DefaultWait(type),
                ct)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case DispatchOutcome.Completed:
            {
                var detail = await _db.GetCommandAsync(commandId, ct).ConfigureAwait(false);
                await _db.AddAuditAsync(
                        new AuditLogEntry(
                            actor,
                            ip,
                            country,
                            "command.complete",
                            machineId,
                            commandId,
                            result.Response?.Status.ToString() ?? "unknown"),
                        ct)
                    .ConfigureAwait(false);
                return detail is null
                    ? new CommandExecutionResult(
                        StatusCodes.Status202Accepted,
                        null,
                        new CommandPendingView(commandId, "Unknown", result.LastStep),
                        null,
                        null)
                    : new CommandExecutionResult(StatusCodes.Status200OK, detail, null, null, null);
            }

            case DispatchOutcome.Running:
            {
                var detail = await _db.GetCommandAsync(commandId, ct).ConfigureAwait(false);
                var lastStep = detail?.Steps.LastOrDefault()?.Step ?? result.LastStep;
                return new CommandExecutionResult(
                    StatusCodes.Status202Accepted,
                    null,
                    new CommandPendingView(commandId, detail?.Status.ToString() ?? "Running", lastStep),
                    null,
                    null);
            }

            case DispatchOutcome.Offline:
                await _db.MarkCommandFailedAsync(
                        commandId,
                        machineId,
                        AcErrorCodes.MachineOffline,
                        "The machine is offline.",
                        ct)
                    .ConfigureAwait(false);
                return new CommandExecutionResult(
                    StatusCodes.Status503ServiceUnavailable,
                    null,
                    null,
                    AcErrorCodes.MachineOffline,
                    $"Machine '{machineId}' is offline.");

            case DispatchOutcome.NoAck:
                await _db.MarkCommandFailedAsync(
                        commandId,
                        machineId,
                        AcErrorCodes.AgentNoAck,
                        "The agent did not acknowledge the command.",
                        ct)
                    .ConfigureAwait(false);
                return new CommandExecutionResult(
                    StatusCodes.Status504GatewayTimeout,
                    null,
                    null,
                    AcErrorCodes.AgentNoAck,
                    "The agent did not acknowledge the command.");

            default:
                await _db.MarkCommandFailedAsync(
                        commandId,
                        machineId,
                        AcErrorCodes.QueryTimeout,
                        "The agent did not complete the request.",
                        ct)
                    .ConfigureAwait(false);
                return new CommandExecutionResult(
                    StatusCodes.Status504GatewayTimeout,
                    null,
                    null,
                    AcErrorCodes.QueryTimeout,
                    "The agent did not complete the request.");
        }
    }

    private TimeSpan DefaultWait(string type) => type switch
    {
        CommandTypes.SessionAbort => TimeSpan.FromSeconds(15),
        CommandTypes.SessionPrompt => TimeSpan.FromSeconds(15),
        CommandTypes.SessionAbortAndPrompt => TimeSpan.FromSeconds(30),
        CommandTypes.VpnConnect => TimeSpan.FromSeconds(120),
        CommandTypes.VpnDisconnect => TimeSpan.FromSeconds(30),
        _ => _options.CommandTimeout,
    };

    private async Task<CommandExecutionResult> ExistingAsync(string commandId, CancellationToken ct)
    {
        var detail = await _db.GetCommandAsync(commandId, ct).ConfigureAwait(false);
        return detail is null
            ? new CommandExecutionResult(
                StatusCodes.Status404NotFound,
                null,
                null,
                AcErrorCodes.CommandNotFound,
                $"Command '{commandId}' was not found.")
            : Existing(detail);
    }

    private static CommandExecutionResult Existing(CommandDetailView detail) =>
        CommandStateMachine.IsTerminal(detail.Status)
            ? new CommandExecutionResult(StatusCodes.Status200OK, detail, null, null, null)
            : new CommandExecutionResult(
                StatusCodes.Status202Accepted,
                null,
                new CommandPendingView(
                    detail.CommandId,
                    detail.Status.ToString(),
                    detail.Steps.LastOrDefault()?.Step),
                null,
                null);
}
