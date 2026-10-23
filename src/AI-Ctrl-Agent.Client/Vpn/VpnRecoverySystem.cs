using System.Collections.Concurrent;
using System.Threading.Channels;
using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Vpn;

public sealed class VpnRecoverySystem : IAcSystem
{
    private readonly Channel<RecoveryTrigger> _queue;
    private readonly ConcurrentDictionary<SessionRef, RecoveryState> _states = new();
    private readonly IVpnRecoveryActions _actions;
    private readonly IVpnRecoveryNotifier _notifier;
    private readonly VpnRecoveryOptions _options;
    private readonly ILogger<VpnRecoverySystem> _logger;
    private AcSystemContext? _context;
    private TimeProvider? _time;
    private IAsyncDisposable? _errorSubscription;
    private IAsyncDisposable? _stallSubscription;
    private IAsyncDisposable? _turnSubscription;
    private int _initialized;

    public VpnRecoverySystem(
        IVpnRecoveryActions actions,
        IVpnRecoveryNotifier notifier,
        VpnRecoveryOptions options,
        ILogger<VpnRecoverySystem> logger)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _actions = actions;
        _notifier = notifier;
        _options = options;
        _logger = logger;
        _queue = Channel.CreateBounded<RecoveryTrigger>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public string Name => "vpn-recovery";

    public IReadOnlyList<string> DependsOn { get; } = ["vpn", "monitor"];

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _context = ctx;
        _time = ctx.Time;
        if (!_options.Enabled || Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _errorSubscription = ctx.Bus.Subscribe<SessionErrorClassifiedEvent>(OnErrorClassifiedAsync);
        _stallSubscription = ctx.Bus.Subscribe<SessionStalledEvent>(OnStalledAsync);
        _turnSubscription = ctx.Bus.Subscribe<SessionTurnCompletedEvent>(OnTurnCompletedAsync);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("VPN recovery is disabled.");
            return ValueTask.CompletedTask;
        }

        var context = _context ?? throw new InvalidOperationException("InitAsync must be called before StartAsync.");
        if (_options.WebhookUri is null)
        {
            _logger.LogWarning(
                "VPN recovery is enabled without a valid HTTPS Discord webhook URL; notifications are disabled.");
        }

        context.Loops.Run("vpn-recovery", RunAsync);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        _queue.Writer.TryComplete();
        await DisposeSubscriptionAsync(Interlocked.Exchange(ref _errorSubscription, null)).ConfigureAwait(false);
        await DisposeSubscriptionAsync(Interlocked.Exchange(ref _stallSubscription, null)).ConfigureAwait(false);
        await DisposeSubscriptionAsync(Interlocked.Exchange(ref _turnSubscription, null)).ConfigureAwait(false);
    }

    private async ValueTask RunAsync(CancellationToken ct)
    {
        await foreach (var trigger in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(trigger, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VPN recovery failed for session {SessionId}.", trigger.Session.SessionId);
                await NotifyAsync(
                    VpnRecoveryNoticeKind.Failed,
                    trigger,
                    errorCode: AcErrorCodes.InternalError,
                    ct: ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(RecoveryTrigger trigger, CancellationToken ct)
    {
        var time = _time ?? throw new InvalidOperationException("InitAsync must be called before processing.");
        var state = _states.GetOrAdd(trigger.Session, _ => new RecoveryState());
        var now = time.GetUtcNow();
        var maxAttempts = Math.Max(1, _options.MaxAttemptsPerSession);
        var exhausted = false;
        var coolingDown = false;

        lock (state.Gate)
        {
            if (state.Attempts >= maxAttempts)
            {
                if (state.ExhaustedNotified)
                {
                    coolingDown = true;
                }
                else
                {
                    state.ExhaustedNotified = true;
                    exhausted = true;
                }
            }
            else if (state.LastAttemptAt is { } last && now - last < _options.Cooldown)
            {
                coolingDown = true;
            }
            else
            {
                state.LastAttemptAt = now;
                state.Attempts++;
            }
        }

        if (coolingDown)
        {
            _logger.LogDebug(
                "VPN recovery for session {SessionId} is cooling down.",
                trigger.Session.SessionId);
            return;
        }

        if (exhausted)
        {
            _logger.LogWarning(
                "VPN recovery reached the attempt limit for session {SessionId}.",
                trigger.Session.SessionId);
            await NotifyAsync(VpnRecoveryNoticeKind.Exhausted, trigger, ct: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await NotifyAsync(trigger.Kind, trigger, ct: ct).ConfigureAwait(false);

            var aborted = await _actions
                .AbortSessionAsync(trigger.Session, ct)
                .ConfigureAwait(false);
            if (!aborted.Succeeded)
            {
                await NotifyAsync(
                    VpnRecoveryNoticeKind.Failed,
                    trigger,
                    aborted.Server,
                    aborted.ErrorCode,
                    ct).ConfigureAwait(false);
                return;
            }

            if (_options.AutoConnectVpn)
            {
                var connected = await _actions
                    .ConnectVpnAsync(trigger.ToContext(), ct)
                    .ConfigureAwait(false);
                if (!connected.Succeeded)
                {
                    await NotifyAsync(
                        VpnRecoveryNoticeKind.Failed,
                        trigger,
                        connected.Server,
                        connected.ErrorCode,
                        ct).ConfigureAwait(false);
                    return;
                }

                await NotifyAsync(
                    VpnRecoveryNoticeKind.VpnConnected,
                    trigger,
                    server: connected.Server,
                    ct: ct).ConfigureAwait(false);
            }

            if (!_options.AutoResume)
            {
                return;
            }

            var resumed = await _actions.ResumeSessionAsync(trigger.Session, ct).ConfigureAwait(false);
            if (!resumed.Succeeded)
            {
                await NotifyAsync(
                    VpnRecoveryNoticeKind.Failed,
                    trigger,
                    resumed.Server,
                    resumed.ErrorCode,
                    ct).ConfigureAwait(false);
                return;
            }

            await NotifyAsync(VpnRecoveryNoticeKind.Resumed, trigger, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (VpnException ex)
        {
            await NotifyAsync(
                VpnRecoveryNoticeKind.Failed,
                trigger,
                errorCode: ex.Code,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VPN recovery action failed for session {SessionId}.", trigger.Session.SessionId);
            await NotifyAsync(
                VpnRecoveryNoticeKind.Failed,
                trigger,
                errorCode: AcErrorCodes.InternalError,
                ct: ct).ConfigureAwait(false);
        }
    }

    private async Task NotifyAsync(
        VpnRecoveryNoticeKind kind,
        RecoveryTrigger trigger,
        string? server = null,
        string? errorCode = null,
        CancellationToken ct = default)
    {
        var notice = new VpnRecoveryNotice(
            kind,
            trigger.Session.Machine.Value,
            trigger.Session,
            server,
            errorCode,
            trigger.StalledFor);
        try
        {
            await _notifier.SendAsync(notice, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN recovery notification failed for {Kind}.", kind);
        }
    }

    private ValueTask OnErrorClassifiedAsync(
        SessionErrorClassifiedEvent classified,
        CancellationToken ct)
    {
        var kind = classified.Error.Class switch
        {
            ErrorClass.QuotaExceeded => VpnRecoveryNoticeKind.QuotaExceeded,
            ErrorClass.RateLimited when _options.HandleRateLimited => VpnRecoveryNoticeKind.RateLimited,
            ErrorClass.Network when _options.HandleStalled => VpnRecoveryNoticeKind.Stalled,
            _ => (VpnRecoveryNoticeKind?)null,
        };
        return kind is null
            ? ValueTask.CompletedTask
            : EnqueueAsync(
                new RecoveryTrigger(
                    classified.Session,
                    kind.Value,
                    StalledFor: null,
                    classified.Error.Class,
                    classified.Error.Message,
                    classified.Retry),
                ct);
    }

    private ValueTask OnStalledAsync(SessionStalledEvent stalled, CancellationToken ct) =>
        _options.HandleStalled
            ? EnqueueAsync(
                new RecoveryTrigger(
                    stalled.Session,
                    VpnRecoveryNoticeKind.Stalled,
                    stalled.StalledFor,
                    ErrorClass.Unknown,
                    Message: null,
                    Retry: null),
                ct)
            : ValueTask.CompletedTask;

    private ValueTask OnTurnCompletedAsync(SessionTurnCompletedEvent completed, CancellationToken ct)
    {
        if (_states.TryGetValue(completed.Session, out var state))
        {
            lock (state.Gate)
            {
                state.Attempts = 0;
                state.ExhaustedNotified = false;
            }
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask EnqueueAsync(RecoveryTrigger trigger, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        if (!_queue.Writer.TryWrite(trigger))
        {
            _logger.LogWarning(
                "VPN recovery queue is closed; dropping {Kind} for session {SessionId}.",
                trigger.Kind,
                trigger.Session.SessionId);
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask DisposeSubscriptionAsync(IAsyncDisposable? subscription)
    {
        if (subscription is not null)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RecoveryState
    {
        public object Gate { get; } = new();

        public DateTimeOffset? LastAttemptAt { get; set; }

        public int Attempts { get; set; }

        public bool ExhaustedNotified { get; set; }
    }

    private sealed record RecoveryTrigger(
        SessionRef Session,
        VpnRecoveryNoticeKind Kind,
        TimeSpan? StalledFor,
        ErrorClass ErrorClass,
        string? Message,
        RetryInfo? Retry)
    {
        public VpnRecoveryContext ToContext() =>
            new(Kind, ErrorClass, Message, Retry, StalledFor);
    }
}
