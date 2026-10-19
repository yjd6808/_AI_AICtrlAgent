using AiCtrl.Client.State;
using AiCtrl.Core.Domain;
using Shouldly;

namespace AiCtrl.Tests.State;

public sealed class SessionStateProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan StallThreshold = TimeSpan.FromMinutes(10);

    [Fact]
    public void Permission_pending_has_highest_priority()
    {
        Project(AcRunState.Idle, permissionPending: true).ShouldBe(SessionDisplayState.WaitingPermission);
    }

    [Fact]
    public void Waiting_permission_run_state_maps_to_waiting_permission()
    {
        Project(AcRunState.WaitingPermission).ShouldBe(SessionDisplayState.WaitingPermission);
    }

    [Fact]
    public void Stalled_busy_session_takes_priority_over_running()
    {
        Project(AcRunState.Busy, idleFor: TimeSpan.FromMinutes(11)).ShouldBe(SessionDisplayState.Stalled);
    }

    [Fact]
    public void Stalled_retrying_session_takes_priority_over_retrying()
    {
        Project(AcRunState.Retrying, idleFor: TimeSpan.FromMinutes(11)).ShouldBe(SessionDisplayState.Stalled);
    }

    [Fact]
    public void New_activity_returns_stalled_session_to_running()
    {
        Project(AcRunState.Busy, idleFor: TimeSpan.FromMinutes(11)).ShouldBe(SessionDisplayState.Stalled);

        Project(AcRunState.Busy, idleFor: TimeSpan.FromSeconds(5)).ShouldBe(SessionDisplayState.Running);
    }

    [Fact]
    public void Retrying_with_quota_error_is_quota_blocked()
    {
        Project(
                AcRunState.Retrying,
                ErrorClass.QuotaExceeded,
                lastErrorAt: Now - TimeSpan.FromMinutes(3))
            .ShouldBe(SessionDisplayState.QuotaBlocked);
    }

    [Fact]
    public void Quota_blocked_is_released_after_successful_turn()
    {
        Project(
                AcRunState.Idle,
                ErrorClass.QuotaExceeded,
                lastErrorAt: Now - TimeSpan.FromMinutes(5),
                lastSuccessAt: Now - TimeSpan.FromMinutes(1))
            .ShouldBe(SessionDisplayState.Idle);
    }

    [Fact]
    public void Quota_error_after_success_is_still_blocked()
    {
        Project(
                AcRunState.Idle,
                ErrorClass.RateLimited,
                lastErrorAt: Now - TimeSpan.FromMinutes(1),
                lastSuccessAt: Now - TimeSpan.FromMinutes(5))
            .ShouldBe(SessionDisplayState.QuotaBlocked);
    }

    [Fact]
    public void Fresh_retrying_session_maps_to_retrying()
    {
        Project(AcRunState.Retrying).ShouldBe(SessionDisplayState.Retrying);
    }

    [Fact]
    public void Fresh_busy_session_maps_to_running()
    {
        Project(AcRunState.Busy).ShouldBe(SessionDisplayState.Running);
    }

    [Fact]
    public void Foreign_busy_takes_priority_over_error()
    {
        Project(AcRunState.ForeignBusy, ErrorClass.ToolError).ShouldBe(SessionDisplayState.ForeignBusy);
    }

    [Fact]
    public void Last_error_class_maps_to_errored()
    {
        Project(AcRunState.Idle, ErrorClass.ToolError).ShouldBe(SessionDisplayState.Errored);
    }

    [Fact]
    public void Aborted_error_maps_to_aborted()
    {
        Project(AcRunState.Idle, ErrorClass.Aborted).ShouldBe(SessionDisplayState.Aborted);
    }

    [Fact]
    public void No_error_and_idle_maps_to_idle()
    {
        Project(AcRunState.Idle).ShouldBe(SessionDisplayState.Idle);
    }

    private static SessionDisplayState Project(
        AcRunState runState,
        ErrorClass lastError = ErrorClass.None,
        bool permissionPending = false,
        TimeSpan? idleFor = null,
        DateTimeOffset? lastErrorAt = null,
        DateTimeOffset? lastSuccessAt = null)
    {
        var lastActivity = idleFor is { } idle ? Now - idle : Now;
        return SessionStateProjector.Project(
            new SessionStateInput(runState, lastError, permissionPending, lastActivity, lastErrorAt, lastSuccessAt),
            Now,
            StallThreshold);
    }
}
