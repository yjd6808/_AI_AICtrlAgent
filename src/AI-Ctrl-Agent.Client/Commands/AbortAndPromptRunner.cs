using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Commands;

public sealed record AbortAndPromptOptions(
    TimeSpan PollInterval,
    TimeSpan AbortWait,
    TimeSpan AbortRetryExtra,
    TimeSpan PromptConfirmWait)
{
    public static AbortAndPromptOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10));
}

public sealed class AbortAndPromptRunner
{
    private const int MaxChildDepth = 3;

    private const int PageSize = 200;

    private readonly AbortAndPromptOptions _options;

    private readonly TimeProvider _time;

    private readonly ILogger<AbortAndPromptRunner> _logger;

    public AbortAndPromptRunner(
        AbortAndPromptOptions options,
        TimeProvider time,
        ILogger<AbortAndPromptRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<CommandResult> RunAsync(
        IProviderInstance instance,
        SessionAbortAndPromptPayload payload,
        string commandId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(payload);

        var sessionId = payload.Session.SessionId;
        var (failure, aborted) = await SettleAsync(instance, sessionId, depth: 0, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var promptStartedAt = _time.GetUtcNow();
        var accepted = await instance
            .PromptAsync(sessionId, new PromptRequest(payload.Text, payload.Model, null, commandId), ct)
            .ConfigureAwait(false);

        var confirmed = await WaitUntilAsync(
            token => IsPromptVisibleAsync(instance, sessionId, accepted, payload.Text, promptStartedAt, token),
            _options.PromptConfirmWait,
            ct).ConfigureAwait(false);

        if (!confirmed)
        {
            _logger.LogWarning("Prompt {CommandId} was not confirmed on session {SessionId}", commandId, sessionId);
            return CommandResult.Unconfirmed(
                AcErrorCodes.PromptUnconfirmed,
                "Prompt was not confirmed within the acceptance window; it was not resent.");
        }

        var result = new AbortAndPromptResult(aborted, accepted.MessageId, accepted.AcceptedAt);
        return CommandResult.Success(JsonSerializer.SerializeToElement(result, AcJsonContext.Default.AbortAndPromptResult));
    }

    private async Task<(CommandResult? Failure, int Aborted)> SettleAsync(
        IProviderInstance instance,
        string sessionId,
        int depth,
        CancellationToken ct)
    {
        var state = await instance.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (state is null)
        {
            return (Failure(AcErrorCodes.SessionNotFound, $"Session '{sessionId}' was not found."), 0);
        }

        if (state.RunState == AcRunState.ForeignBusy)
        {
            return (Failure(AcErrorCodes.ForeignTurn, $"Session '{sessionId}' is busy with a foreign turn."), 0);
        }

        var aborted = 0;
        if (IsBusy(state.RunState))
        {
            aborted++;
            await TryAbortAsync(instance, sessionId, ct).ConfigureAwait(false);

            var outcome = await WaitIdleAsync(instance, sessionId, _options.AbortWait, ct).ConfigureAwait(false);
            if (outcome == IdleOutcome.Gone)
            {
                return (Failure(AcErrorCodes.SessionNotFound, $"Session '{sessionId}' was not found."), aborted);
            }

            if (outcome == IdleOutcome.Foreign)
            {
                return (Failure(AcErrorCodes.ForeignTurn, $"Session '{sessionId}' is busy with a foreign turn."), aborted);
            }

            if (outcome == IdleOutcome.TimedOut)
            {
                await TryAbortAsync(instance, sessionId, ct).ConfigureAwait(false);
                outcome = await WaitIdleAsync(instance, sessionId, _options.AbortRetryExtra, ct).ConfigureAwait(false);
                if (outcome == IdleOutcome.Gone)
                {
                    return (Failure(AcErrorCodes.SessionNotFound, $"Session '{sessionId}' was not found."), aborted);
                }

                if (outcome == IdleOutcome.Foreign)
                {
                    return (Failure(AcErrorCodes.ForeignTurn, $"Session '{sessionId}' is busy with a foreign turn."), aborted);
                }

                if (outcome == IdleOutcome.TimedOut)
                {
                    return (Failure(AcErrorCodes.AbortTimeout, $"Session '{sessionId}' did not become idle after abort."), aborted);
                }
            }
        }

        if (depth < MaxChildDepth)
        {
            var children = await GetBusyChildrenAsync(instance, sessionId, ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                var (childFailure, childAborted) = await SettleAsync(instance, child, depth + 1, ct).ConfigureAwait(false);
                aborted += childAborted;
                if (childFailure is not null)
                {
                    return (childFailure, aborted);
                }
            }
        }

        if (!await IsSettledAsync(instance, sessionId, ct).ConfigureAwait(false))
        {
            return (Failure(AcErrorCodes.NotSettled, $"Session '{sessionId}' has an unfinished assistant turn."), aborted);
        }

        return (null, aborted);
    }

    private static async Task<IReadOnlyList<string>> GetBusyChildrenAsync(
        IProviderInstance instance,
        string sessionId,
        CancellationToken ct)
    {
        var all = await instance
            .ListSessionsAsync(new SessionQuery(instance.Info.Id, SessionListViews.All, IncludeChildren: true, Cursor: null), ct)
            .ConfigureAwait(false);

        var children = new List<string>();
        foreach (var candidate in all)
        {
            if (candidate.ParentId != sessionId || candidate.Ref.SessionId == sessionId)
            {
                continue;
            }

            var busy =
                candidate.RunState is AcRunState.Busy or AcRunState.Retrying or AcRunState.WaitingPermission;
            if (busy)
            {
                children.Add(candidate.Ref.SessionId);
            }
        }

        return children;
    }

    private static bool IsBusy(AcRunState state) =>
        state is AcRunState.Busy or AcRunState.Retrying or AcRunState.WaitingPermission;

    private async Task<IdleOutcome> WaitIdleAsync(
        IProviderInstance instance,
        string sessionId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            var state = await instance.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            if (state is null)
            {
                return IdleOutcome.Gone;
            }

            if (state.RunState == AcRunState.ForeignBusy)
            {
                return IdleOutcome.Foreign;
            }

            if (!IsBusy(state.RunState))
            {
                return IdleOutcome.Idle;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return IdleOutcome.TimedOut;
            }

            var delay = remaining < _options.PollInterval ? remaining : _options.PollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private async Task TryAbortAsync(IProviderInstance instance, string sessionId, CancellationToken ct)
    {
        try
        {
            await instance.AbortAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Abort failed for session {SessionId}; continuing", sessionId);
        }
    }

    private static async Task<bool> IsSettledAsync(
        IProviderInstance instance,
        string sessionId,
        CancellationToken ct)
    {
        var messages = await ReadAllMessagesAsync(instance, sessionId, ct).ConfigureAwait(false);
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != "assistant")
            {
                continue;
            }

            return message.CompletedAt is not null || message.Error is not null;
        }

        return true;
    }

    private static async Task<bool> IsPromptVisibleAsync(
        IProviderInstance instance,
        string sessionId,
        PromptAccepted accepted,
        string text,
        DateTimeOffset promptStartedAt,
        CancellationToken ct)
    {
        var messages = await ReadAllMessagesAsync(instance, sessionId, ct).ConfigureAwait(false);
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != "user")
            {
                continue;
            }

            if (message.MessageId == accepted.MessageId)
            {
                return true;
            }

            return message.CreatedAt >= promptStartedAt
                && message.Parts.Any(part => part.Type == "text" && part.Text == text);
        }

        return false;
    }

    private static async Task<IReadOnlyList<MessageView>> ReadAllMessagesAsync(
        IProviderInstance instance,
        string sessionId,
        CancellationToken ct)
    {
        var all = new List<MessageView>();
        string? cursor = null;
        while (true)
        {
            var page = await instance
                .GetMessagesAsync(sessionId, new PageRequest(cursor, PageSize), ct)
                .ConfigureAwait(false);
            all.AddRange(page.Items);

            if (!page.HasMore || page.NextCursor is null || page.NextCursor == cursor)
            {
                return all;
            }

            cursor = page.NextCursor;
        }
    }

    private async Task<bool> WaitUntilAsync(
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            if (await predicate(ct).ConfigureAwait(false))
            {
                return true;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var delay = remaining < _options.PollInterval ? remaining : _options.PollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static CommandResult Failure(string code, string message) =>
        CommandResult.Failure(code, message);

    private enum IdleOutcome
    {
        Idle,
        TimedOut,
        Gone,
        Foreign,
    }
}
