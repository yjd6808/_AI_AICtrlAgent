namespace AiCtrl.Core.Runtime;

public abstract record AcEvent
{
    public required string EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
