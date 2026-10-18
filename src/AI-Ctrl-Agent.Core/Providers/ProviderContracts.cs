using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Providers;

public interface ICodingAgentProvider
{
    ProviderKind Kind { get; }

    ValueTask<IReadOnlyList<IProviderInstance>> DiscoverAsync(CancellationToken ct);
}

public interface IProviderInstance : IAsyncDisposable
{
    InstanceInfo Info { get; }

    CapabilitySet Capabilities { get; }

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionQuery q, CancellationToken ct);

    ValueTask<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken ct);

    ValueTask<Page<MessageView>> GetMessagesAsync(string sessionId, PageRequest page, CancellationToken ct);

    ValueTask<IReadOnlyList<ModelOption>> ListModelsAsync(CancellationToken ct);

    ValueTask<AbortResult> AbortAsync(string sessionId, CancellationToken ct);

    ValueTask<PromptAccepted> PromptAsync(string sessionId, PromptRequest req, CancellationToken ct);

    ValueTask ReplyPermissionAsync(string sessionId, string permissionId, PermissionReply reply, CancellationToken ct);

    IAsyncEnumerable<ProviderEvent> WatchAsync(CancellationToken ct);
}
