using System.Diagnostics;
using System.Text.Json;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.Client.Vpn;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Core.Workspace;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Link;

public sealed class ClientRequestHandler
{
    private readonly CommandSystem _commands;

    private readonly JournalStore _journal;

    private readonly IProviderInstanceRegistry _registry;

    private readonly StateSystem _state;

    private readonly FilesSystem _files;

    private readonly TimeProvider _time;

    private readonly ILogger<ClientRequestHandler> _logger;

    private readonly VpnSystem? _vpn;

    public ClientRequestHandler(
        CommandSystem commands,
        JournalStore journal,
        IProviderInstanceRegistry registry,
        StateSystem state,
        FilesSystem files,
        TimeProvider time,
        ILogger<ClientRequestHandler> logger,
        VpnSystem? vpn = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _commands = commands;
        _journal = journal;
        _registry = registry;
        _state = state;
        _files = files;
        _time = time;
        _logger = logger;
        _vpn = vpn;
    }

    public async Task<ResponsePacket> HandleAsync(
        RequestPacket packet,
        Func<AckPacket, Task>? acknowledge,
        CancellationToken ct,
        Func<ProgressPacket, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var started = Stopwatch.GetTimestamp();
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = packet.TraceId,
            ["RequestId"] = packet.RequestId,
            ["Type"] = packet.Type,
        });

        try
        {
            if (packet.Kind == RequestKind.Query)
            {
                var data = await QueryAsync(packet, ct).ConfigureAwait(false);
                return new ResponsePacket(
                    packet.RequestId,
                    ResponseStatus.Succeeded,
                    data,
                    null,
                    null,
                    _time.GetUtcNow(),
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var envelope = new CommandEnvelope(
                packet.RequestId,
                _registry.MachineId,
                packet.Type,
                packet.Payload,
                packet.IssuedBy,
                packet.IssuedAt,
                packet.ExpiresAt,
                packet.Attempt);
            var result = await _commands
                .ExecuteAsync(
                    envelope,
                    async token =>
                    {
                        if (acknowledge is not null)
                        {
                            await acknowledge(new AckPacket(packet.RequestId, _time.GetUtcNow())).ConfigureAwait(false);
                        }
                    },
                    progress,
                    ct)
                .ConfigureAwait(false);

            return new ResponsePacket(
                packet.RequestId,
                Map(result.Status),
                result.Result,
                result.ErrorCode,
                result.ErrorMessage,
                _time.GetUtcNow(),
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (QueryException ex)
        {
            return Failure(packet, started, ex.Code, ex.Message);
        }
        catch (WorkspacePathException ex)
        {
            return Failure(packet, started, ex.Code, ex.Message);
        }
        catch (WorkspaceException ex)
        {
            return Failure(packet, started, ex.Code, ex.Message);
        }
        catch (VpnException ex)
        {
            return Failure(packet, started, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request {RequestId} failed.", packet.RequestId);
            return new ResponsePacket(
                packet.RequestId,
                ResponseStatus.Failed,
                null,
                AcErrorCodes.InternalError,
                ex.Message,
                _time.GetUtcNow(),
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public Task<IReadOnlyList<string>> GetOpenIdsAsync(CancellationToken ct) => _journal.GetOpenIdsAsync(ct);

    public Task MarkReportedAsync(string commandId, CancellationToken ct) =>
        _journal.MarkReportedAsync(commandId, ct);

    public async Task ReportUnreportedAsync(Func<ResponsePacket, Task> respond, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(respond);
        var entries = await _journal.GetUnreportedTerminalAsync(200, ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            JsonElement? data = null;
            if (!string.IsNullOrEmpty(entry.ResultJson))
            {
                using var document = JsonDocument.Parse(entry.ResultJson);
                data = document.RootElement.Clone();
            }

            var response = new ResponsePacket(
                entry.CommandId,
                Map(entry.Status),
                data,
                entry.ErrorCode,
                entry.ErrorMessage,
                entry.UpdatedAt,
                (long)Math.Max(0, (entry.UpdatedAt - entry.ReceivedAt).TotalMilliseconds));
            await respond(response).ConfigureAwait(false);
            await _journal.MarkReportedAsync(entry.CommandId, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement?> QueryAsync(RequestPacket packet, CancellationToken ct)
    {
        switch (packet.Type)
        {
            case QueryTypes.WorkspaceList:
            {
                var sessions = await LoadSessionsAsync(ct).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    _files.GetWorkspaceInfos(sessions),
                    AcJsonContext.Default.ListWorkspaceInfo);
            }

            case QueryTypes.FilesList:
            {
                var query = Deserialize(packet, AcJsonContext.Default.FileListQuery)
                    ?? throw new QueryException(AcErrorCodes.InvalidPayload, "files.list payload is required.");
                return JsonSerializer.SerializeToElement(
                    _files.List(query),
                    AcJsonContext.Default.FileListPage);
            }

            case QueryTypes.FilesStat:
            {
                var query = Deserialize(packet, AcJsonContext.Default.FileStatQuery)
                    ?? throw new QueryException(AcErrorCodes.InvalidPayload, "files.stat payload is required.");
                return JsonSerializer.SerializeToElement(
                    await _files.StatAsync(query, ct).ConfigureAwait(false),
                    AcJsonContext.Default.FileStatView);
            }

            case QueryTypes.FilesPreview:
            {
                var query = Deserialize(packet, AcJsonContext.Default.FilePreviewQuery)
                    ?? throw new QueryException(AcErrorCodes.InvalidPayload, "files.preview payload is required.");
                return JsonSerializer.SerializeToElement(
                    _files.Preview(query),
                    AcJsonContext.Default.FilePreviewView);
            }

            case QueryTypes.AgentInstances:
            {
                var query = Deserialize(packet, AcJsonContext.Default.AgentInstancesQuery);
                var instances = _registry.Instances
                    .Where(instance => query?.Kind is null
                        || string.Equals(instance.Info.Kind.ToString(), query.Kind, StringComparison.OrdinalIgnoreCase))
                    .Select(instance => instance.Info)
                    .ToList();
                return JsonSerializer.SerializeToElement(instances, AcJsonContext.Default.ListInstanceInfo);
            }

            case QueryTypes.SessionList:
            {
                var query = Deserialize(packet, AcJsonContext.Default.SessionListQuery)
                    ?? new SessionListQuery(null, SessionListViews.Active, true, null);
                var view = query.View is SessionListViews.Active or SessionListViews.All
                    ? query.View
                    : throw new QueryException(AcErrorCodes.InvalidPayload, $"Unknown session view '{query.View}'.");
                IProviderInstance? target = null;
                if (!string.IsNullOrWhiteSpace(query.Instance))
                {
                    target = ResolveInstance(query.Instance)
                        ?? throw new QueryException(AcErrorCodes.InstanceNotFound, $"Instance '{query.Instance}' is not available.");
                }

                var now = _time.GetUtcNow();
                var sessions = new List<SessionSummary>();
                foreach (var instance in target is null ? _registry.Instances : new[] { target })
                {
                    var items = await instance
                        .ListSessionsAsync(
                            new SessionQuery(instance.Info.Id, view, query.IncludeChildren, query.Cursor),
                            ct)
                        .ConfigureAwait(false);
                    sessions.AddRange(items.Select(item => item with
                    {
                        DisplayState = _state.ProjectDisplayState(item.Ref, item, now),
                    }));
                }

                sessions.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
                return JsonSerializer.SerializeToElement(
                    new SessionPageDto(sessions, null, false),
                    AcJsonContext.Default.SessionPageDto);
            }

            case QueryTypes.SessionGet:
            {
                var query = Deserialize(packet, AcJsonContext.Default.SessionGetQuery)
                    ?? throw new QueryException(AcErrorCodes.InvalidPayload, "session.get payload is required.");
                var instance = ResolveSessionInstance(query.Session)
                    ?? throw new QueryException(
                        AcErrorCodes.InstanceNotFound,
                        $"Instance '{query.Session.Instance.Value}' is not available.");
                var session = await instance.GetSessionAsync(query.Session.SessionId, ct).ConfigureAwait(false)
                    ?? throw new QueryException(
                        AcErrorCodes.SessionNotFound,
                        $"Session '{query.Session.SessionId}' was not found.");
                return JsonSerializer.SerializeToElement(session, AcJsonContext.Default.SessionSummary);
            }

            case QueryTypes.SessionMessages:
            {
                var query = Deserialize(packet, AcJsonContext.Default.SessionMessagesQuery)
                    ?? throw new QueryException(AcErrorCodes.InvalidPayload, "session.messages payload is required.");
                var instance = ResolveSessionInstance(query.Session)
                    ?? throw new QueryException(
                        AcErrorCodes.InstanceNotFound,
                        $"Instance '{query.Session.Instance.Value}' is not available.");
                var page = await instance
                    .GetMessagesAsync(
                        query.Session.SessionId,
                        new PageRequest(query.Cursor, Math.Clamp(query.Limit, 1, 200)),
                        ct)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    new MessagePageDto(page.Items, page.NextCursor, page.HasMore),
                    AcJsonContext.Default.MessagePageDto);
            }

            case QueryTypes.SessionModels:
            {
                var query = Deserialize(packet, AcJsonContext.Default.SessionModelsQuery);
                IProviderInstance? target = null;
                if (!string.IsNullOrWhiteSpace(query?.Instance))
                {
                    target = ResolveInstance(query.Instance)
                        ?? throw new QueryException(AcErrorCodes.InstanceNotFound, $"Instance '{query.Instance}' is not available.");
                }

                var models = new List<ModelOption>();
                foreach (var instance in target is null ? _registry.Instances : new[] { target })
                {
                    models.AddRange(await instance.ListModelsAsync(ct).ConfigureAwait(false));
                }

                return JsonSerializer.SerializeToElement(models, AcJsonContext.Default.ListModelOption);
            }

            case QueryTypes.VpnPage:
            {
                var query = Deserialize(packet, AcJsonContext.Default.VpnPageQuery)
                    ?? new VpnPageQuery(null, null);
                return JsonSerializer.SerializeToElement(
                    await VpnOrThrow().GetPageAsync(query, ct).ConfigureAwait(false),
                    AcJsonContext.Default.VpnPageSnapshot);
            }

            case QueryTypes.VpnStatus:
                return JsonSerializer.SerializeToElement(
                    await VpnOrThrow().GetStatusAsync(ct).ConfigureAwait(false),
                    AcJsonContext.Default.VpnStatusView);

            case QueryTypes.VpnCountries:
                return JsonSerializer.SerializeToElement(
                    await VpnOrThrow().GetCountriesAsync(ct).ConfigureAwait(false),
                    AcJsonContext.Default.ListVpnCountryView);

            case QueryTypes.VpnServers:
            {
                var query = Deserialize(packet, AcJsonContext.Default.VpnServersQuery)
                    ?? throw new QueryException(AcErrorCodes.InvalidPayload, "vpn.servers payload is required.");
                return JsonSerializer.SerializeToElement(
                    await VpnOrThrow().GetServersAsync(query, ct).ConfigureAwait(false),
                    AcJsonContext.Default.VpnServerPage);
            }

            case QueryTypes.MachineFeatures:
                return JsonSerializer.SerializeToElement(BuildFeatures(), AcJsonContext.Default.ListMachineFeature);

            default:
                throw new QueryException(
                    AcErrorCodes.UnsupportedCommand,
                    $"Query type '{packet.Type}' is not supported by the client.");
        }
    }

    private async Task<List<SessionSummary>> LoadSessionsAsync(CancellationToken ct)
    {
        var sessions = new List<SessionSummary>();
        foreach (var instance in _registry.Instances)
        {
            var items = await instance
                .ListSessionsAsync(
                    new SessionQuery(instance.Info.Id, SessionListViews.All, IncludeChildren: true, Cursor: null),
                    ct)
                .ConfigureAwait(false);
            sessions.AddRange(items);
        }

        return sessions;
    }

    private ResponsePacket Failure(RequestPacket packet, long started, string code, string message) =>
        new(
            packet.RequestId,
            ResponseStatus.Failed,
            null,
            code,
            message,
            _time.GetUtcNow(),
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private IProviderInstance? ResolveInstance(string instanceId) =>
        _registry.Instances.FirstOrDefault(instance => string.Equals(
            instance.Info.Id.Value,
            instanceId,
            StringComparison.Ordinal));

    private IProviderInstance? ResolveSessionInstance(SessionRef session) =>
        session.Machine == _registry.MachineId ? ResolveInstance(session.Instance.Value) : null;

    private VpnSystem VpnOrThrow() =>
        _vpn ?? throw new QueryException(AcErrorCodes.CapabilityMissing, "VPN is not available.");

    private IReadOnlyList<MachineFeature> BuildFeatures()
    {
        var available = _registry.Instances.Count > 0;
        var features = new List<MachineFeature>
        {
            new MachineFeature(
                "sessions",
                FeatureSection.Agents,
                "Sessions",
                "/sessions",
                available,
                available ? null : "No coding agent instance is available.",
                null),
            new MachineFeature(
                "commands",
                FeatureSection.Etc,
                "Commands",
                "/commands",
                true,
                null,
                null),
        };

        if (_vpn is not null)
        {
            var vpnAvailable = _vpn.Enabled && _vpn.CliFound;
            features.Add(new MachineFeature(
                "vpn.protonvpn",
                FeatureSection.Etc,
                "ProtonVPN",
                "/vpn",
                vpnAvailable,
                !_vpn.Enabled
                    ? "VPN is disabled in client configuration."
                    : _vpn.CliFound ? null : "protonvpn CLI was not found.",
                null));
        }

        return features;
    }

    private static T? Deserialize<T>(RequestPacket packet, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        packet.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : JsonSerializer.Deserialize(packet.Payload, typeInfo);

    private static ResponseStatus Map(CommandStatus status) => status switch
    {
        CommandStatus.Succeeded => ResponseStatus.Succeeded,
        CommandStatus.Failed => ResponseStatus.Failed,
        CommandStatus.Cancelled => ResponseStatus.Cancelled,
        CommandStatus.Expired => ResponseStatus.Expired,
        _ => ResponseStatus.Unknown,
    };

    private sealed class QueryException : Exception
    {
        public QueryException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
