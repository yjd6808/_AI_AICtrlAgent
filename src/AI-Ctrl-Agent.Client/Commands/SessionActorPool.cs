using System.Collections.Concurrent;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Commands;

public sealed class SessionActorPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<SessionRef, SessionActor> _actors = new();
    private readonly ILoggerFactory _logs;

    public SessionActorPool(ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        _logs = logs;
    }

    public ValueTask<CommandResult> ExecuteAsync(
        SessionRef session,
        string type,
        Func<CancellationToken, ValueTask<CommandResult>> work,
        CancellationToken ct) =>
        GetOrCreate(session).ExecuteAsync(type, work, ct);

    public SessionActor GetOrCreate(SessionRef session) =>
        _actors.GetOrAdd(session, value => new SessionActor(value, _logs.CreateLogger<SessionActor>()));

    public async ValueTask DisposeAsync()
    {
        foreach (var actor in _actors.Values)
        {
            await actor.DisposeAsync().ConfigureAwait(false);
        }

        _actors.Clear();
    }
}
