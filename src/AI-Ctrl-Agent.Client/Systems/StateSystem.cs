using System.Collections.Concurrent;
using AiCtrl.Client.State;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed record SessionStateSnapshot(
    SessionRef Session,
    AcRunState RunState,
    RetryInfo? Retry,
    ErrorInfo? LastError,
    SessionDisplayState DisplayState,
    DateTimeOffset? BusySince,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset UpdatedAt);

public sealed class StateSystem : IAcSystem
{
    private readonly ConcurrentDictionary<SessionRef, TrackedState> _states = new();

    private readonly TimeProvider _time;

    private readonly MonitorOptions _options;

    private readonly List<IAsyncDisposable> _subscriptions = new();

    public StateSystem(TimeProvider time, MonitorOptions options, ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logs);
        _time = time;
        _options = options;
    }

    public string Name => "state";

    public IReadOnlyList<string> DependsOn { get; } = new[] { "monitor" };

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
    {
        _subscriptions.Add(ctx.Bus.Subscribe<SessionStatusChangedEvent>((e, _) =>
        {
            ApplyStatus(e);
            return ValueTask.CompletedTask;
        }));

        _subscriptions.Add(ctx.Bus.Subscribe<SessionTurnCompletedEvent>((e, _) =>
        {
            ApplyTurnCompleted(e);
            return ValueTask.CompletedTask;
        }));

        _subscriptions.Add(ctx.Bus.Subscribe<SessionErrorClassifiedEvent>((e, _) =>
        {
            ApplyError(e);
            return ValueTask.CompletedTask;
        }));

        _subscriptions.Add(ctx.Bus.Subscribe<SessionPermissionRequestedEvent>((e, _) =>
        {
            ApplyPermissionRequested(e);
            return ValueTask.CompletedTask;
        }));

        _subscriptions.Add(ctx.Bus.Subscribe<MessageUpsertedEvent>((e, _) =>
        {
            ApplyMessage(e);
            return ValueTask.CompletedTask;
        }));

        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken ct)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
    }

    public IReadOnlyList<SessionStateSnapshot> GetSnapshots()
    {
        var now = _time.GetUtcNow();
        return _states.Select(pair => CreateSnapshot(pair.Key, pair.Value, now)).ToList();
    }

    public SessionStateSnapshot? GetSnapshot(SessionRef session)
    {
        return _states.TryGetValue(session, out var state)
            ? CreateSnapshot(session, state, _time.GetUtcNow())
            : null;
    }

    public SessionDisplayState ProjectDisplayState(SessionRef session, SessionSummary summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!_states.TryGetValue(session, out var tracked))
        {
            return SessionStateProjector.Project(
                new SessionStateInput(
                    summary.RunState,
                    summary.LastError,
                    summary.RunState == AcRunState.WaitingPermission,
                    summary.LastActivityAt,
                    null,
                    null),
                now,
                _options.StallThreshold);
        }

        lock (tracked.Gate)
        {
            var lastError = summary.LastError != ErrorClass.None
                ? summary.LastError
                : tracked.LastError?.Class ?? ErrorClass.None;

            return SessionStateProjector.Project(
                new SessionStateInput(
                    summary.RunState,
                    lastError,
                    tracked.Permissions.Count > 0 || summary.RunState == AcRunState.WaitingPermission,
                    tracked.LastActivityAt ?? summary.LastActivityAt,
                    tracked.LastErrorAt,
                    tracked.LastSuccessAt),
                now,
                _options.StallThreshold);
        }
    }

    private SessionStateSnapshot CreateSnapshot(SessionRef session, TrackedState state, DateTimeOffset now)
    {
        lock (state.Gate)
        {
            var display = SessionStateProjector.Project(
                new SessionStateInput(
                    state.RunState,
                    state.LastError?.Class ?? ErrorClass.None,
                    state.Permissions.Count > 0 || state.RunState == AcRunState.WaitingPermission,
                    state.LastActivityAt,
                    state.LastErrorAt,
                    state.LastSuccessAt),
                now,
                _options.StallThreshold);

            return new SessionStateSnapshot(
                session,
                state.RunState,
                state.Retry,
                state.LastError,
                display,
                state.BusySince,
                state.LastActivityAt,
                state.UpdatedAt);
        }
    }

    private void ApplyStatus(SessionStatusChangedEvent status)
    {
        var state = GetOrCreate(status.Session);
        var now = status.OccurredAt;
        lock (state.Gate)
        {
            if (status.RunState == AcRunState.Busy && state.RunState != AcRunState.Busy)
            {
                state.BusySince = now;
            }

            if (status.RunState is AcRunState.Idle or AcRunState.Unknown)
            {
                state.BusySince = null;
            }

            if (status.RunState == AcRunState.Idle)
            {
                state.Permissions.Clear();
            }

            state.RunState = status.RunState;
            state.Retry = status.Retry;
            state.LastActivityAt = now;
            state.UpdatedAt = now;
        }
    }

    private void ApplyTurnCompleted(SessionTurnCompletedEvent completed)
    {
        var state = GetOrCreate(completed.Session);
        var now = completed.OccurredAt;
        lock (state.Gate)
        {
            state.LastSuccessAt = now;
            state.LastError = null;
            state.LastErrorAt = null;
            state.Permissions.Clear();
            state.BusySince = null;
            state.LastActivityAt = now;
            state.UpdatedAt = now;
        }
    }

    private void ApplyError(SessionErrorClassifiedEvent error)
    {
        var state = GetOrCreate(error.Session);
        var now = error.OccurredAt;
        lock (state.Gate)
        {
            state.LastError = error.Error;
            state.LastErrorAt = now;
            state.LastActivityAt = now;
            state.UpdatedAt = now;
        }
    }

    private void ApplyPermissionRequested(SessionPermissionRequestedEvent permission)
    {
        var state = GetOrCreate(permission.Session);
        var now = permission.OccurredAt;
        lock (state.Gate)
        {
            state.Permissions.Add(permission.PermissionId);
            state.LastActivityAt = now;
            state.UpdatedAt = now;
        }
    }

    private void ApplyMessage(MessageUpsertedEvent message)
    {
        var state = GetOrCreate(message.Session);
        var now = message.OccurredAt;
        lock (state.Gate)
        {
            state.LastActivityAt = now;
            state.UpdatedAt = now;

            if (message.Message.Error is { } error)
            {
                state.LastError = error;
                state.LastErrorAt = now;
            }
        }
    }

    private TrackedState GetOrCreate(SessionRef session) =>
        _states.GetOrAdd(session, _ => new TrackedState());

    private sealed class TrackedState
    {
        public object Gate { get; } = new();

        public AcRunState RunState { get; set; } = AcRunState.Idle;

        public RetryInfo? Retry { get; set; }

        public ErrorInfo? LastError { get; set; }

        public DateTimeOffset? LastErrorAt { get; set; }

        public DateTimeOffset? LastSuccessAt { get; set; }

        public DateTimeOffset? LastActivityAt { get; set; }

        public DateTimeOffset? BusySince { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);
    }
}
