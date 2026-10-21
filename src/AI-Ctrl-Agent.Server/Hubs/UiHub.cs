using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Projection;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AiCtrl.Server.Hubs;

[Authorize(Policy = GoogleAuthPolicy.BrowserPolicy)]
public sealed class UiHub : Hub<IUiClient>, IUiHub
{
    private readonly ServerDb _db;

    private readonly ConnectionRegistry _connections;

    private readonly SessionWatchCoordinator _watches;

    public UiHub(ServerDb db, ConnectionRegistry connections, SessionWatchCoordinator watches)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(watches);
        _db = db;
        _connections = connections;
        _watches = watches;
    }

    public override async Task OnConnectedAsync()
    {
        var machines = await _db.GetMachinesAsync(Context.ConnectionAborted).ConfigureAwait(false);
        var views = machines
            .Select(machine => MachineViewFactory.Create(machine, _connections))
            .ToList();
        await Clients.Caller.FleetSnapshot(new FleetSnapshotDto(views)).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public async Task WatchSession(MachineId machine, string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(machine, sessionId))
            .ConfigureAwait(false);
        await _watches.WatchUiAsync(machine.Value, sessionId, Context.ConnectionId, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public async Task UnwatchSession(MachineId machine, string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(machine, sessionId))
            .ConfigureAwait(false);
        await _watches.UnwatchUiAsync(machine.Value, sessionId, Context.ConnectionId, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _watches.RemoveConnectionAsync(Context.ConnectionId, CancellationToken.None).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public static string SessionGroup(MachineId machine, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(machine.Value))
        {
            throw new ArgumentException("Machine ID is required.", nameof(machine));
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }

        return $"s:{machine.Value}:{sessionId}";
    }
}
