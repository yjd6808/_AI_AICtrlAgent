using AiCtrl.Core.Domain;

namespace AiCtrl.Client.State;

public sealed record SessionStateInput(
    AcRunState RunState,
    ErrorClass LastError,
    bool PermissionPending,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? LastSuccessAt);

public static class SessionStateProjector
{
    public static SessionDisplayState Project(
        SessionStateInput input,
        DateTimeOffset now,
        TimeSpan stallThreshold)
    {
        if (input.PermissionPending || input.RunState == AcRunState.WaitingPermission)
        {
            return SessionDisplayState.WaitingPermission;
        }

        var busy = input.RunState is AcRunState.Busy or AcRunState.Retrying;
        if (busy && input.LastActivityAt is { } lastActivity && now - lastActivity >= stallThreshold)
        {
            return SessionDisplayState.Stalled;
        }

        var quotaBlocked = input.LastError is ErrorClass.QuotaExceeded or ErrorClass.RateLimited;
        if (quotaBlocked && !SucceededAfterError(input))
        {
            return SessionDisplayState.QuotaBlocked;
        }

        switch (input.RunState)
        {
            case AcRunState.Retrying:
                return SessionDisplayState.Retrying;
            case AcRunState.Busy:
                return SessionDisplayState.Running;
            case AcRunState.ForeignBusy:
                return SessionDisplayState.ForeignBusy;
        }

        if (input.LastError == ErrorClass.Aborted)
        {
            return SessionDisplayState.Aborted;
        }

        if (input.LastError != ErrorClass.None && !quotaBlocked)
        {
            return SessionDisplayState.Errored;
        }

        return SessionDisplayState.Idle;
    }

    private static bool SucceededAfterError(SessionStateInput input) =>
        input.LastSuccessAt is { } success &&
        (input.LastErrorAt is null || success > input.LastErrorAt);
}
