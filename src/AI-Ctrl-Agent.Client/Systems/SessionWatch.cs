using AiCtrl.Core.Domain;

namespace AiCtrl.Client.Systems;

internal sealed class SessionWatch
{
    private const int MaxRememberedMessages = 256;

    private readonly HashSet<string> _loggedParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roleByMessage = new(StringComparer.Ordinal);
    private readonly Queue<string> _roleOrder = new();

    public SessionWatch(DateTimeOffset now)
    {
        LastEventAt = now;
    }

    public AcRunState LastRunState { get; private set; } = AcRunState.Unknown;

    public DateTimeOffset LastEventAt { get; private set; }

    public DateTimeOffset? BusySince { get; private set; }

    public bool StallReported { get; private set; }

    public bool LastAssistantCompleted { get; set; }

    public string? LastAssistantMessageId { get; set; }

    public ModelRef? LastModel { get; set; }

    public void Touch(DateTimeOffset now)
    {
        LastEventAt = now;
        StallReported = false;
    }

    public (AcRunState Previous, DateTimeOffset? BusySince) ApplyState(AcRunState state, DateTimeOffset now)
    {
        var previous = LastRunState;
        if (state == AcRunState.Busy && previous != AcRunState.Busy)
        {
            BusySince = now;
        }

        var busySince = BusySince;
        if (state != AcRunState.Busy)
        {
            BusySince = null;
        }

        LastRunState = state;
        Touch(now);
        return (previous, busySince);
    }

    public void MarkStallReported() => StallReported = true;

    public void RememberRole(string messageId, string role)
    {
        if (_roleByMessage.ContainsKey(messageId))
        {
            _roleByMessage[messageId] = role;
            return;
        }

        _roleByMessage[messageId] = role;
        _roleOrder.Enqueue(messageId);
        while (_roleOrder.Count > MaxRememberedMessages)
        {
            _roleByMessage.Remove(_roleOrder.Dequeue());
        }
    }

    public string? RoleOf(string messageId) =>
        _roleByMessage.TryGetValue(messageId, out var role) ? role : null;

    public bool IsPartLogged(string partId) => _loggedParts.Contains(partId);

    public void MarkPartLogged(string partId) => _loggedParts.Add(partId);
}
