using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Runtime;
using AiCtrl.Core.Serialization;
using AiCtrl.Core.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed class LocalApiSystem : IAcSystem
{
    private const string PathPrefix = "/local/v1";

    private const int MaxBodyPageSize = 200;

    private static readonly string Version =
        typeof(LocalApiSystem).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(LocalApiSystem).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly CommandSystem _commands;

    private readonly IProviderInstanceRegistry _registry;

    private readonly StateSystem _state;

    private readonly FilesSystem _files;

    private readonly LocalApiOptions _options;

    private readonly LocalTokenStore _tokens;

    private readonly TimeProvider _time;

    private readonly Func<bool>? _serverLinkConnected;

    private readonly ILogger<LocalApiSystem> _logger;

    private WebApplication? _app;

    private CancellationTokenSource? _lifetime;

    private string _token = string.Empty;

    private int _port;

    public LocalApiSystem(
        CommandSystem commands,
        IProviderInstanceRegistry registry,
        StateSystem state,
        FilesSystem files,
        LocalApiOptions options,
        TimeProvider time,
        ILoggerFactory logs,
        Func<bool>? serverLinkConnected = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logs);
        _commands = commands;
        _registry = registry;
        _state = state;
        _files = files;
        _options = options;
        _tokens = new LocalTokenStore(options.TokenPath);
        _time = time;
        _serverLinkConnected = serverLinkConnected;
        _logger = logs.CreateLogger<LocalApiSystem>();
        _port = options.Port;
    }

    public string Name => "local-api";

    public IReadOnlyList<string> DependsOn { get; } = new[] { "state" };

    public int Port => _port;

    public string TokenPath => _options.TokenPath;

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask StartAsync(CancellationToken ct)
    {
        _token = await _tokens.GetOrCreateAsync(ct).ConfigureAwait(false);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, _options.Port));

        _app = builder.Build();
        _app.Use(GuardAsync);
        MapEndpoints(_app);

        await _app.StartAsync(ct).ConfigureAwait(false);
        _port = ResolvePort();
        _logger.LogInformation("Local API listening on http://127.0.0.1:{Port}{Prefix}", _port, PathPrefix);
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        _lifetime?.Cancel();

        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(PathPrefix))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!IsAllowedHost(context.Request.Host))
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    AcErrorCodes.InvalidHost,
                    "The Host header does not match the local API endpoint.")
                .ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(context.Request.Headers.Authorization.ToString()))
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    AcErrorCodes.Unauthorized,
                    "A valid local token is required.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local API request {Method} {Path} failed", context.Request.Method, context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await WriteProblemAsync(
                        context,
                        StatusCodes.Status500InternalServerError,
                        AcErrorCodes.InternalError,
                        "The local API request failed.")
                    .ConfigureAwait(false);
            }
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet(
            $"{PathPrefix}/status",
            (HttpContext context) =>
            {
                var started = Stopwatch.GetTimestamp();
                var status = new LocalStatusDto(
                    _registry.MachineId,
                    Version,
                    ServerConnected: _serverLinkConnected?.Invoke() ?? false,
                    _port,
                    _registry.Instances.Count,
                    _state.GetSnapshots().Count);

                return Json(
                    new ApiResponse<LocalStatusDto>(status, Meta(context, started)),
                    AcJsonContext.Default.ApiResponseLocalStatusDto);
            });

        app.MapGet(
            $"{PathPrefix}/instances",
            (HttpContext context) =>
            {
                var started = Stopwatch.GetTimestamp();
                var instances = _registry.Instances.Select(instance => instance.Info).ToList();

                return Json(
                    new ApiResponse<List<InstanceInfo>>(instances, Meta(context, started)),
                    AcJsonContext.Default.ApiResponseListInstanceInfo);
            });

        app.MapGet(
            $"{PathPrefix}/instances/{{instanceId}}/sessions",
            async (HttpContext context, string instanceId, string? view, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var instance = ResolveInstance(instanceId);
                if (instance is null)
                {
                    return InstanceMissing(context, instanceId);
                }

                var resolvedView = view ?? SessionListViews.Active;
                if (!IsValidView(resolvedView))
                {
                    return InvalidView(context, resolvedView);
                }

                var page = await ListSessionsAsync(instance, resolvedView, ct).ConfigureAwait(false);
                return Json(
                    new ApiResponse<SessionPageDto>(page, Meta(context, started)),
                    AcJsonContext.Default.ApiResponseSessionPageDto);
            });

        app.MapGet(
            $"{PathPrefix}/sessions",
            async (HttpContext context, string? instance, string? view, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                IProviderInstance? target = null;
                if (!string.IsNullOrWhiteSpace(instance))
                {
                    target = ResolveInstance(instance);
                    if (target is null)
                    {
                        return InstanceMissing(context, instance);
                    }
                }

                var resolvedView = view ?? SessionListViews.Active;
                if (!IsValidView(resolvedView))
                {
                    return InvalidView(context, resolvedView);
                }

                var page = await ListSessionsAsync(target, resolvedView, ct).ConfigureAwait(false);
                return Json(
                    new ApiResponse<SessionPageDto>(page, Meta(context, started)),
                    AcJsonContext.Default.ApiResponseSessionPageDto);
            });

        app.MapGet(
            $"{PathPrefix}/sessions/{{sessionId}}/messages",
            async (HttpContext context, string sessionId, string? instance, int? limit, string? cursor, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var resolved = await ResolveSessionAsync(sessionId, instance, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    return SessionMissing(context, sessionId);
                }

                var pageSize = Math.Clamp(limit ?? 20, 1, MaxBodyPageSize);
                var page = await resolved.Value.Instance
                    .GetMessagesAsync(sessionId, new PageRequest(cursor, pageSize), ct)
                    .ConfigureAwait(false);

                return Json(
                    new ApiResponse<MessagePageDto>(
                        new MessagePageDto(page.Items, page.NextCursor, page.HasMore),
                        Meta(context, started)),
                    AcJsonContext.Default.ApiResponseMessagePageDto);
            });

        app.MapPost(
            $"{PathPrefix}/sessions/{{sessionId}}/abort",
            async (HttpContext context, string sessionId, string? instance, CancellationToken ct) =>
            {
                var resolved = await ResolveSessionAsync(sessionId, instance, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    return SessionMissing(context, sessionId);
                }

                AbortSessionBody? body = null;
                if (context.Request.HasJsonContentType())
                {
                    try
                    {
                        body = await context.Request
                            .ReadFromJsonAsync(AcJsonContext.Default.AbortSessionBody, ct)
                            .ConfigureAwait(false);
                    }
                    catch (JsonException ex)
                    {
                        return InvalidPayload(context, ex.Message);
                    }
                }

                var payload = JsonSerializer.SerializeToElement(
                    new SessionAbortPayload(resolved.Value.Ref, body?.IncludeChildren ?? true),
                    AcJsonContext.Default.SessionAbortPayload);

                return await ExecuteCommandAsync(context, CommandTypes.SessionAbort, payload, ct).ConfigureAwait(false);
            });

        app.MapPost(
            $"{PathPrefix}/sessions/{{sessionId}}/prompt",
            async (HttpContext context, string sessionId, string? instance, CancellationToken ct) =>
            {
                var resolved = await ResolveSessionAsync(sessionId, instance, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    return SessionMissing(context, sessionId);
                }

                PromptBody? body;
                try
                {
                    body = context.Request.HasJsonContentType()
                        ? await context.Request.ReadFromJsonAsync(AcJsonContext.Default.PromptBody, ct).ConfigureAwait(false)
                        : null;
                }
                catch (JsonException ex)
                {
                    return InvalidPayload(context, ex.Message);
                }

                if (body is null || string.IsNullOrWhiteSpace(body.Text))
                {
                    return InvalidPayload(context, "The prompt text is required.");
                }

                var payload = JsonSerializer.SerializeToElement(
                    new SessionPromptPayload(resolved.Value.Ref, body.Text, body.Model, body.Agent),
                    AcJsonContext.Default.SessionPromptPayload);

                return await ExecuteCommandAsync(context, CommandTypes.SessionPrompt, payload, ct).ConfigureAwait(false);
            });

        app.MapPost(
            $"{PathPrefix}/sessions/{{sessionId}}/abort-and-prompt",
            async (HttpContext context, string sessionId, string? instance, CancellationToken ct) =>
            {
                var resolved = await ResolveSessionAsync(sessionId, instance, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    return SessionMissing(context, sessionId);
                }

                AbortAndPromptBody? body;
                try
                {
                    body = context.Request.HasJsonContentType()
                        ? await context.Request
                            .ReadFromJsonAsync(AcJsonContext.Default.AbortAndPromptBody, ct)
                            .ConfigureAwait(false)
                        : null;
                }
                catch (JsonException ex)
                {
                    return InvalidPayload(context, ex.Message);
                }

                if (body is null || string.IsNullOrWhiteSpace(body.Text))
                {
                    return InvalidPayload(context, "The prompt text is required.");
                }

                var payload = JsonSerializer.SerializeToElement(
                    new SessionAbortAndPromptPayload(resolved.Value.Ref, body.Text, body.Model),
                    AcJsonContext.Default.SessionAbortAndPromptPayload);

                return await ExecuteCommandAsync(context, CommandTypes.SessionAbortAndPrompt, payload, ct)
                    .ConfigureAwait(false);
            });

        app.MapGet(
            $"{PathPrefix}/commands/{{commandId}}",
            async (HttpContext context, string commandId, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var view = await _commands.GetViewAsync(commandId, ct).ConfigureAwait(false);
                if (view is null)
                {
                    return Problem(
                        context,
                        StatusCodes.Status404NotFound,
                        AcErrorCodes.CommandNotFound,
                        $"Command '{commandId}' was not found.");
                }

                return Json(
                    new ApiResponse<CommandView>(view, Meta(context, started)),
                    AcJsonContext.Default.ApiResponseCommandView);
            });

        app.MapGet(
            $"{PathPrefix}/workspaces",
            async (HttpContext context, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var sessions = await ListSessionsAsync(null, SessionListViews.All, ct).ConfigureAwait(false);
                return Json(
                    new ApiResponse<List<WorkspaceInfo>>(
                        _files.GetWorkspaceInfos(sessions.Sessions).ToList(),
                        Meta(context, started)),
                    AcJsonContext.Default.ApiResponseListWorkspaceInfo);
            });

        app.MapGet(
            $"{PathPrefix}/files",
            (HttpContext context, string workspaceId, string? path, string? sort, string? cursor, int? limit) =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var page = _files.List(new FileListQuery(
                        workspaceId,
                        path,
                        sort ?? FileSorts.Name,
                        cursor,
                        limit ?? 500));
                    return Json(
                        new ApiResponse<FileListPage>(page, Meta(context, started)),
                        AcJsonContext.Default.ApiResponseFileListPage);
                }
                catch (WorkspacePathException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
                catch (WorkspaceException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
            });

        app.MapGet(
            $"{PathPrefix}/files/stat",
            async (HttpContext context, string workspaceId, string path, bool? hash, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var stat = await _files
                        .StatAsync(new FileStatQuery(workspaceId, path, hash ?? false), ct)
                        .ConfigureAwait(false);
                    return Json(
                        new ApiResponse<FileStatView>(stat, Meta(context, started)),
                        AcJsonContext.Default.ApiResponseFileStatView);
                }
                catch (WorkspacePathException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
                catch (WorkspaceException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
            });

        app.MapGet(
            $"{PathPrefix}/files/preview",
            (HttpContext context, string workspaceId, string path) =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var preview = _files.Preview(new FilePreviewQuery(workspaceId, path));
                    return Json(
                        new ApiResponse<FilePreviewView>(preview, Meta(context, started)),
                        AcJsonContext.Default.ApiResponseFilePreviewView);
                }
                catch (WorkspacePathException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
                catch (WorkspaceException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
            });

        app.MapPost(
            $"{PathPrefix}/files/mkdir",
            async (HttpContext context, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var payload = await ReadBodyAsync(context, AcJsonContext.Default.FilesMkdirPayload, ct).ConfigureAwait(false);
                if (payload.Payload is null)
                {
                    return InvalidPayload(context, payload.Error ?? "files.mkdir payload is required.");
                }

                try
                {
                    var stat = _files.Mkdir(payload.Payload);
                    return Json(
                        new ApiResponse<FileStatView>(stat, Meta(context, started)),
                        AcJsonContext.Default.ApiResponseFileStatView);
                }
                catch (WorkspacePathException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
                catch (WorkspaceException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
            });

        app.MapPost(
            $"{PathPrefix}/files/delete",
            async (HttpContext context, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var payload = await ReadBodyAsync(context, AcJsonContext.Default.FilesDeletePayload, ct).ConfigureAwait(false);
                if (payload.Payload is null)
                {
                    return InvalidPayload(context, payload.Error ?? "files.delete payload is required.");
                }

                try
                {
                    var result = _files.Delete(payload.Payload);
                    return Json(
                        new ApiResponse<FileMutationResult>(result, Meta(context, started)),
                        AcJsonContext.Default.ApiResponseFileMutationResult);
                }
                catch (WorkspacePathException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
                catch (WorkspaceException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
            });

        app.MapPost(
            $"{PathPrefix}/files/rename",
            async (HttpContext context, CancellationToken ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                var payload = await ReadBodyAsync(context, AcJsonContext.Default.FilesRenamePayload, ct).ConfigureAwait(false);
                if (payload.Payload is null)
                {
                    return InvalidPayload(context, payload.Error ?? "files.rename payload is required.");
                }

                try
                {
                    var stat = _files.Rename(payload.Payload);
                    return Json(
                        new ApiResponse<FileStatView>(stat, Meta(context, started)),
                        AcJsonContext.Default.ApiResponseFileStatView);
                }
                catch (WorkspacePathException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
                catch (WorkspaceException ex)
                {
                    return WorkspaceProblem(context, ex.Code, ex.Message);
                }
            });
    }

    private static async Task<(T? Payload, string? Error)> ReadBodyAsync<T>(
        HttpContext context,
        JsonTypeInfo<T> info,
        CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType())
        {
            return (default, "A JSON request body is required.");
        }

        try
        {
            return (await context.Request.ReadFromJsonAsync(info, ct).ConfigureAwait(false), null);
        }
        catch (JsonException ex)
        {
            return (default, ex.Message);
        }
    }

    private static IResult WorkspaceProblem(HttpContext context, string code, string detail)
    {
        var status = code switch
        {
            AcErrorCodes.PathDenied => StatusCodes.Status403Forbidden,
            AcErrorCodes.WorkspaceNotAllowed => StatusCodes.Status404NotFound,
            AcErrorCodes.FileConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Problem(context, status, code, detail);
    }

    private async Task<IResult> ExecuteCommandAsync(
        HttpContext context,
        string type,
        JsonElement payload,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var issuedAt = _time.GetUtcNow();
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new CommandEnvelope(
            commandId,
            _registry.MachineId,
            type,
            payload,
            "local",
            issuedAt,
            issuedAt.AddMinutes(5),
            Attempt: 0);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        CommandResult result;
        try
        {
            result = await _commands.ExecuteAsync(envelope, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Problem(
                context,
                StatusCodes.Status504GatewayTimeout,
                AcErrorCodes.QueryTimeout,
                "The command did not complete within the local API timeout.");
        }

        var view = await _commands.GetViewAsync(commandId, ct).ConfigureAwait(false)
            ?? new CommandView(
                commandId,
                type,
                result.Status,
                result.Result,
                result.ErrorCode,
                result.ErrorMessage,
                "local",
                issuedAt,
                _time.GetUtcNow());

        return Json(
            new ApiResponse<CommandView>(view, Meta(context, started)),
            AcJsonContext.Default.ApiResponseCommandView);
    }

    private async Task<SessionPageDto> ListSessionsAsync(
        IProviderInstance? instance,
        string view,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var targets = instance is null ? _registry.Instances : new[] { instance };
        var sessions = new List<SessionSummary>();

        foreach (var target in targets)
        {
            var items = await target
                .ListSessionsAsync(
                    new SessionQuery(target.Info.Id, view, IncludeChildren: true, Cursor: null),
                    ct)
                .ConfigureAwait(false);

            sessions.AddRange(items.Select(item => item with
            {
                DisplayState = _state.ProjectDisplayState(item.Ref, item, now),
            }));
        }

        sessions.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));

        return new SessionPageDto(sessions, NextCursor: null, HasMore: false);
    }

    private async ValueTask<(IProviderInstance Instance, SessionRef Ref)?> ResolveSessionAsync(
        string sessionId,
        string? instanceId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            var instance = ResolveInstance(instanceId);
            return instance is null
                ? null
                : (instance, new SessionRef(_registry.MachineId, instance.Info.Id, sessionId));
        }

        foreach (var instance in _registry.Instances)
        {
            var summary = await instance.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            if (summary is not null)
            {
                return (instance, summary.Ref);
            }
        }

        return null;
    }

    private IProviderInstance? ResolveInstance(string instanceId) =>
        _registry.Instances.FirstOrDefault(instance => string.Equals(
            instance.Info.Id.Value,
            instanceId,
            StringComparison.Ordinal));

    private static bool IsValidView(string view) =>
        view is SessionListViews.Active or SessionListViews.All;

    private bool IsAllowedHost(HostString host) =>
        (string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        && host.Port == _port;

    private bool IsAuthorized(string header)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(_token);

        return provided.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private int ResolvePort()
    {
        var server = _app!.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();

        return address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? uri.Port
            : _options.Port;
    }

    private static IResult Json<TValue>(TValue value, JsonTypeInfo<TValue> info) =>
        Results.Json(value, info, "application/json", StatusCodes.Status200OK);

    private static IResult Problem(HttpContext context, int status, string code, string detail) =>
        Results.Json(
            new AcProblemDetails("about:blank", code, status, detail, context.Request.Path, code),
            AcJsonContext.Default.AcProblemDetails,
            "application/problem+json",
            status);

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new AcProblemDetails("about:blank", code, status, detail, context.Request.Path, code);
        return context.Response.WriteAsJsonAsync(
            problem,
            AcJsonContext.Default.AcProblemDetails,
            "application/problem+json",
            context.RequestAborted);
    }

    private static IResult InstanceMissing(HttpContext context, string instanceId) =>
        Problem(
            context,
            StatusCodes.Status404NotFound,
            AcErrorCodes.InstanceNotFound,
            $"Instance '{instanceId}' is not available.");

    private static IResult SessionMissing(HttpContext context, string sessionId) =>
        Problem(
            context,
            StatusCodes.Status404NotFound,
            AcErrorCodes.SessionNotFound,
            $"Session '{sessionId}' was not found.");

    private static IResult InvalidView(HttpContext context, string view) =>
        Problem(
            context,
            StatusCodes.Status400BadRequest,
            AcErrorCodes.InvalidPayload,
            $"Unknown session view '{view}'.");

    private static IResult InvalidPayload(HttpContext context, string detail) =>
        Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, detail);

    private ResponseMeta Meta(HttpContext context, long started) =>
        new(
            MetaSources.Live,
            _time.GetUtcNow(),
            MachineOnline: true,
            Stale: false,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            context.TraceIdentifier);
}
