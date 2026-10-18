namespace AiCtrl.FakeOpenCode;

public sealed class FakeOpenCodeScenario
{
    private int _sseDropAfterEvents;
    private int _ignoreAbortCount;

    public string Username { get; set; } = "opencode";

    public string Password { get; set; } = "test-pw";

    public TimeSpan PromptAcceptDelay { get; set; } = TimeSpan.Zero;

    public bool DropPromptAcceptance { get; set; }

    public TimeSpan BusyDuration { get; set; } = TimeSpan.FromMilliseconds(120);

    public TimeSpan ChunkDelay { get; set; } = TimeSpan.FromMilliseconds(15);

    public int ChunkCount { get; set; } = 3;

    public bool EmitPartDelta { get; set; }

    public bool LoopQuotaRetry { get; set; }

    public TimeSpan QuotaRetryDelay { get; set; } = TimeSpan.FromMilliseconds(40);

    public string QuotaMessage { get; set; } = "Free usage limit reached";

    public int IgnoreAbortCount
    {
        get => Volatile.Read(ref _ignoreAbortCount);
        set => Volatile.Write(ref _ignoreAbortCount, value);
    }

    public int SseDropAfterEvents
    {
        get => Volatile.Read(ref _sseDropAfterEvents);
        set => Volatile.Write(ref _sseDropAfterEvents, value);
    }

    public string? DocJson { get; set; }

    public bool ConsumeIgnoreAbort()
    {
        var current = Volatile.Read(ref _ignoreAbortCount);
        while (current > 0)
        {
            if (Interlocked.CompareExchange(ref _ignoreAbortCount, current - 1, current) == current)
            {
                return true;
            }

            current = Volatile.Read(ref _ignoreAbortCount);
        }

        return false;
    }

    public bool ShouldDropSse(int sentEvents)
    {
        var current = Volatile.Read(ref _sseDropAfterEvents);
        while (current > 0 && sentEvents >= current)
        {
            if (Interlocked.CompareExchange(ref _sseDropAfterEvents, 0, current) == current)
            {
                return true;
            }

            current = Volatile.Read(ref _sseDropAfterEvents);
        }

        return false;
    }
}
