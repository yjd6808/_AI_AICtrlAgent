using AiCtrl.Core.Domain;
using AiCtrl.Core.Runtime;

namespace AiCtrl.Core.Events;

public sealed record SessionStatusChangedEvent : AcEvent
{
    public required SessionRef Session { get; init; }

    public required AcRunState RunState { get; init; }

    public RetryInfo? Retry { get; init; }
}

public sealed record SessionTurnCompletedEvent : AcEvent
{
    public required SessionRef Session { get; init; }

    public string? MessageId { get; init; }

    public TimeSpan? Duration { get; init; }
}

public sealed record SessionStalledEvent : AcEvent
{
    public required SessionRef Session { get; init; }

    public required TimeSpan StalledFor { get; init; }
}

public sealed record SessionErrorClassifiedEvent : AcEvent
{
    public required SessionRef Session { get; init; }

    public required ErrorInfo Error { get; init; }

    public RetryInfo? Retry { get; init; }
}

public sealed record SessionPermissionRequestedEvent : AcEvent
{
    public required SessionRef Session { get; init; }

    public required string PermissionId { get; init; }

    public required string Permission { get; init; }

    public required IReadOnlyList<string> Patterns { get; init; }
}

public sealed record MessageUpsertedEvent : AcEvent
{
    public required SessionRef Session { get; init; }

    public required MessageView Message { get; init; }
}
