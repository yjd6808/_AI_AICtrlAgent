using System.Collections.Concurrent;
using System.Text.Json;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Commands;

public sealed class CommandSystem
{
    private readonly JournalStore _journal;

    private readonly SessionActorPool _actors;

    private readonly IProviderInstanceRegistry _registry;

    private readonly AbortAndPromptRunner _runner;

    private readonly TimeProvider _time;

    private readonly ILogger<CommandSystem> _logger;

    private readonly ConcurrentDictionary<string, Task<CommandResult>> _inFlight = new(StringComparer.Ordinal);

    public CommandSystem(
        JournalStore journal,
        SessionActorPool actors,
        IProviderInstanceRegistry registry,
        AbortAndPromptRunner runner,
        TimeProvider time,
        ILogger<CommandSystem> logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _journal = journal;
        _actors = actors;
        _registry = registry;
        _runner = runner;
        _time = time;
        _logger = logger;
    }

    public Task<CommandResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken ct) =>
        ExecuteAsync(envelope, onAcknowledged: null, ct);

    public async Task<CommandResult> ExecuteAsync(
        CommandEnvelope envelope,
        Func<CancellationToken, Task>? onAcknowledged,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        while (true)
        {
            var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(envelope.CommandId, completion.Task))
            {
                _ = RunOwnerAsync(envelope, completion, onAcknowledged, ct);
                return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            if (!_inFlight.TryGetValue(envelope.CommandId, out var existing))
            {
                continue;
            }

            return await existing.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<CommandView?> GetViewAsync(string commandId, CancellationToken ct)
    {
        var entry = await _journal.GetAsync(commandId, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        return new CommandView(
            entry.CommandId,
            entry.Type,
            entry.Status,
            ParseResult(entry.ResultJson),
            entry.ErrorCode,
            entry.ErrorMessage,
            "local",
            entry.ReceivedAt,
            entry.UpdatedAt);
    }

    private async Task RunOwnerAsync(
        CommandEnvelope envelope,
        TaskCompletionSource<CommandResult> completion,
        Func<CancellationToken, Task>? onAcknowledged,
        CancellationToken ct)
    {
        try
        {
            var created = await _journal.CreateIfAbsentAsync(envelope, ct).ConfigureAwait(false);
            if (!created.Created)
            {
                completion.TrySetResult(Replay(created.Entry));
                return;
            }

            if (onAcknowledged is not null)
            {
                try
                {
                    await onAcknowledged(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Command {CommandId} acknowledgement callback failed.", envelope.CommandId);
                }
            }

            await _journal
                .UpdateAsync(envelope.CommandId, CommandStatus.Running, null, null, null, _time.GetUtcNow(), ct)
                .ConfigureAwait(false);

            CommandResult result;
            try
            {
                result = await DispatchAsync(envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command {CommandId} failed", envelope.CommandId);
                result = CommandResult.Failure(AcErrorCodes.InternalError, ex.Message);
            }

            try
            {
                await _journal
                    .UpdateAsync(
                        envelope.CommandId,
                        result.Status,
                        result.Result,
                        result.ErrorCode,
                        result.ErrorMessage,
                        _time.GetUtcNow(),
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist result of command {CommandId}", envelope.CommandId);
            }

            completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandId} could not be accepted", envelope.CommandId);
            completion.TrySetResult(CommandResult.Failure(AcErrorCodes.InternalError, ex.Message));
        }
        finally
        {
            _inFlight.TryRemove(envelope.CommandId, out _);
        }
    }

    private async Task<CommandResult> DispatchAsync(CommandEnvelope envelope, CancellationToken ct)
    {
        try
        {
            switch (envelope.Type)
            {
                case CommandTypes.SessionAbort:
                {
                    var payload = JsonSerializer.Deserialize(envelope.Payload, AcJsonContext.Default.SessionAbortPayload);
                    if (payload is null)
                    {
                        return CommandResult.Failure(AcErrorCodes.InvalidPayload, "session.abort payload is required.");
                    }

                    var instance = Resolve(payload.Session.Instance);
                    if (instance is null)
                    {
                        return InstanceMissing(payload.Session.Instance);
                    }

                    return await _actors.ExecuteAsync(payload.Session, envelope.Type, async token =>
                    {
                        var result = await instance.AbortAsync(payload.Session.SessionId, token).ConfigureAwait(false);
                        return CommandResult.Success(
                            JsonSerializer.SerializeToElement(result, AcJsonContext.Default.AbortResult));
                    }, ct).ConfigureAwait(false);
                }

                case CommandTypes.SessionPrompt:
                {
                    var payload = JsonSerializer.Deserialize(envelope.Payload, AcJsonContext.Default.SessionPromptPayload);
                    if (payload is null)
                    {
                        return CommandResult.Failure(AcErrorCodes.InvalidPayload, "session.prompt payload is required.");
                    }

                    var instance = Resolve(payload.Session.Instance);
                    if (instance is null)
                    {
                        return InstanceMissing(payload.Session.Instance);
                    }

                    return await _actors.ExecuteAsync(payload.Session, envelope.Type, async token =>
                    {
                        var accepted = await instance
                            .PromptAsync(
                                payload.Session.SessionId,
                                new PromptRequest(payload.Text, payload.Model, payload.Agent, envelope.CommandId),
                                token)
                            .ConfigureAwait(false);
                        return CommandResult.Success(
                            JsonSerializer.SerializeToElement(accepted, AcJsonContext.Default.PromptAccepted));
                    }, ct).ConfigureAwait(false);
                }

                case CommandTypes.SessionAbortAndPrompt:
                {
                    var payload = JsonSerializer.Deserialize(envelope.Payload, AcJsonContext.Default.SessionAbortAndPromptPayload);
                    if (payload is null)
                    {
                        return CommandResult.Failure(AcErrorCodes.InvalidPayload, "session.abort_and_prompt payload is required.");
                    }

                    var instance = Resolve(payload.Session.Instance);
                    if (instance is null)
                    {
                        return InstanceMissing(payload.Session.Instance);
                    }

                    return await _actors.ExecuteAsync(
                        payload.Session,
                        envelope.Type,
                        token => new ValueTask<CommandResult>(_runner.RunAsync(instance, payload, envelope.CommandId, token)),
                        ct).ConfigureAwait(false);
                }

                case CommandTypes.SessionPermissionReply:
                {
                    var payload = JsonSerializer.Deserialize(envelope.Payload, AcJsonContext.Default.SessionPermissionReplyPayload);
                    if (payload is null)
                    {
                        return CommandResult.Failure(AcErrorCodes.InvalidPayload, "session.permission.reply payload is required.");
                    }

                    var reply = payload.Response switch
                    {
                        PermissionResponses.Once => PermissionReply.Once,
                        PermissionResponses.Always => PermissionReply.Always,
                        PermissionResponses.Reject => PermissionReply.Reject,
                        _ => (PermissionReply?)null,
                    };

                    if (reply is null)
                    {
                        return CommandResult.Failure(
                            AcErrorCodes.InvalidPayload,
                            $"Unknown permission response '{payload.Response}'.");
                    }

                    var instance = Resolve(payload.Session.Instance);
                    if (instance is null)
                    {
                        return InstanceMissing(payload.Session.Instance);
                    }

                    return await _actors.ExecuteAsync(payload.Session, envelope.Type, async token =>
                    {
                        await instance
                            .ReplyPermissionAsync(payload.Session.SessionId, payload.PermissionId, reply.Value, token)
                            .ConfigureAwait(false);
                        return CommandResult.Success(null);
                    }, ct).ConfigureAwait(false);
                }

                case CommandTypes.InstanceRefresh:
                    return CommandResult.Success(null);

                default:
                    return CommandResult.Failure(
                        AcErrorCodes.UnsupportedCommand,
                        $"Command type '{envelope.Type}' is not supported by the client.");
            }
        }
        catch (JsonException ex)
        {
            return CommandResult.Failure(AcErrorCodes.InvalidPayload, ex.Message);
        }
    }

    private IProviderInstance? Resolve(InstanceId instanceId) =>
        _registry.Instances.FirstOrDefault(instance => instance.Info.Id == instanceId);

    private static CommandResult InstanceMissing(InstanceId instanceId) =>
        CommandResult.Failure(AcErrorCodes.InstanceNotFound, $"Instance '{instanceId.Value}' is not available.");

    private static CommandResult Replay(JournalEntry entry)
    {
        if (CommandStateMachine.IsTerminal(entry.Status))
        {
            return new CommandResult(entry.Status, ParseResult(entry.ResultJson), entry.ErrorCode, entry.ErrorMessage);
        }

        return new CommandResult(
            entry.Status,
            ParseResult(entry.ResultJson),
            entry.ErrorCode ?? AcErrorCodes.DuplicateCommand,
            entry.ErrorMessage ?? "Command was already accepted but has not completed.");
    }

    private static JsonElement? ParseResult(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
