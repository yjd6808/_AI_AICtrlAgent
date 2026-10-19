using System.Runtime.CompilerServices;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Tests.Commands;

internal sealed class ScriptedProviderInstance : IProviderInstance
{
    private readonly object _gate = new();

    public ScriptedProviderInstance(string instanceId = "opencode:test")
    {
        Info = new InstanceInfo(
            new InstanceId(instanceId),
            ProviderKind.OpenCode,
            "http://127.0.0.1:4096",
            "1.18.34",
            InstanceMode.SharedStore,
            Healthy: true,
            Managed: false,
            CapabilitySet.ListSessions | CapabilitySet.ReadMessages);
    }

    public InstanceInfo Info { get; }

    public CapabilitySet Capabilities => Info.Capabilities;

    public Dictionary<string, SessionSummary> Sessions { get; } = new(StringComparer.Ordinal);

    public List<MessageView> Messages { get; } = new();

    public List<PromptRequest> Prompts { get; } = new();

    public List<string> Aborted { get; } = new();

    public Func<string, int, ValueTask<AbortResult>>? OnAbort { get; set; }

    public Func<PromptRequest, bool> AcceptPrompt { get; set; } = _ => true;

    public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? PromptGate { get; set; }

    public TimeProvider Time { get; set; } = TimeProvider.System;

    public SessionSummary AddSession(string sessionId, AcRunState state, string? parentId = null)
    {
        var summary = new SessionSummary(
            new SessionRef(new MachineId("m_test"), Info.Id, sessionId),
            sessionId,
            parentId,
            "C:\\workspace",
            state,
            ErrorClass.None,
            null,
            Time.GetUtcNow(),
            null,
            null,
            SessionDisplayState.Idle,
            null,
            null,
            0,
            true);
        Sessions[sessionId] = summary;
        return summary;
    }

    public void SetState(string sessionId, AcRunState state) =>
        Sessions[sessionId] = Sessions[sessionId] with { RunState = state };

    public void AddAssistantMessage(string sessionId, bool completed)
    {
        lock (_gate)
        {
            Messages.Add(new MessageView(
                $"msg_assistant_{Messages.Count}",
                "assistant",
                Time.GetUtcNow(),
                completed ? Time.GetUtcNow() : null,
                null,
                Array.Empty<PartView>(),
                null));
        }
    }

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionQuery q, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<SessionSummary>>(Sessions.Values.ToList());

    public ValueTask<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken ct) =>
        ValueTask.FromResult(Sessions.GetValueOrDefault(sessionId));

    public ValueTask<Page<MessageView>> GetMessagesAsync(string sessionId, PageRequest page, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(new Page<MessageView>(Messages.ToList(), null, false));
        }
    }

    public ValueTask<IReadOnlyList<ModelOption>> ListModelsAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ModelOption>>(Array.Empty<ModelOption>());

    public async ValueTask<AbortResult> AbortAsync(string sessionId, CancellationToken ct)
    {
        Aborted.Add(sessionId);
        if (OnAbort is not null)
        {
            return await OnAbort(sessionId, Aborted.Count(id => id == sessionId));
        }

        if (Sessions.TryGetValue(sessionId, out var session))
        {
            Sessions[sessionId] = session with { RunState = AcRunState.Idle };
        }

        return new AbortResult(true, null);
    }

    public async ValueTask<PromptAccepted> PromptAsync(string sessionId, PromptRequest req, CancellationToken ct)
    {
        lock (_gate)
        {
            Prompts.Add(req);
        }

        PromptStarted.TrySetResult();

        if (PromptGate is not null)
        {
            await PromptGate.Task.WaitAsync(ct);
        }

        var acceptedAt = Time.GetUtcNow();
        var messageId = req.ClientMessageId ?? $"msg_user_{Prompts.Count}";
        if (AcceptPrompt(req))
        {
            lock (_gate)
            {
                Messages.Add(new MessageView(
                    messageId,
                    "user",
                    acceptedAt,
                    null,
                    null,
                    new[] { new PartView("part_1", "text", req.Text, null) },
                    null));
            }
        }

        return new PromptAccepted(messageId, acceptedAt);
    }

    public ValueTask ReplyPermissionAsync(
        string sessionId,
        string permissionId,
        PermissionReply reply,
        CancellationToken ct) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProviderEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
