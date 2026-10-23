using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Logging;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Projection;
using AiCtrl.Server.Requests;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;

namespace AiCtrl.Server.Endpoints;

public static class ServerEndpoints
{
    public const string EnrollRateLimit = "enroll";

    public const string AgentAuthRateLimit = "agent-auth";

    public static void MapAcServerEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/health", () => Results.Ok(new { version = typeof(ServerEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0" }))
            .AllowAnonymous();

        app.MapGet("/api/v1/auth/login", LoginAsync)
            .AllowAnonymous();

        app.MapGet("/api/v1/auth/me", (Delegate)Me)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/enroll", EnrollAsync)
            .AllowAnonymous()
            .RequireRateLimiting(EnrollRateLimit);

        app.MapPost("/api/v1/agent/challenge", ChallengeAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AgentAuthRateLimit);

        app.MapPost("/api/v1/agent/token", TokenAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AgentAuthRateLimit);

        app.MapPost("/api/v1/machines/enrollment-codes", CreateEnrollmentCodeAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapDelete("/api/v1/machines/{machineId}", RevokeMachineAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines", ListMachinesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}", GetMachineAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/instances", GetInstancesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/sessions", ListSessionsAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/sessions/{sessionId}", GetSessionAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/sessions/{sessionId}/messages", GetMessagesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/sessions/{sessionId}/stream", GetStreamAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/models", GetModelsAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/features", GetFeaturesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/vpn/page", GetVpnPageAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/vpn", GetVpnStatusAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/vpn/status", GetVpnStatusAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/vpn/countries", GetVpnCountriesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/vpn/servers", GetVpnServersAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/vpn/connect", ConnectVpnAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/vpn/disconnect", DisconnectVpnAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/instances/refresh", RefreshInstancesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/sessions/{sessionId}/abort", AbortSessionAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/sessions/{sessionId}/prompt", PromptSessionAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/sessions/{sessionId}/abort-and-prompt", AbortAndPromptSessionAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/sessions/{sessionId}/permissions/{requestId}", ReplyPermissionAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/commands", ListCommandsAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/commands/{commandId}", GetCommandAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/audit", ListAuditAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/logs/server", ListServerLogsAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/logs/server/{name}", ReadServerLogAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapAcWorkspaceEndpoints();
    }

    public static IServiceCollection AddAcRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(EnrollRateLimit, PartitionByIp);
            options.AddPolicy(AgentAuthRateLimit, PartitionByIp);
        });
        return services;
    }

    private static RateLimitPartition<string> PartitionByIp(HttpContext context)
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        IAuthenticationSchemeProvider schemes)
    {
        if (await schemes.GetSchemeAsync(GoogleDefaults.AuthenticationScheme).ConfigureAwait(false) is null)
        {
            return Problem(context, StatusCodes.Status503ServiceUnavailable, AcErrorCodes.AuthUnavailable, "Google authentication is not configured.");
        }

        var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault()
            ?? context.Request.Query["ReturnUrl"].FirstOrDefault();
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            returnUrl = "/";
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl },
            [GoogleDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult> Me(HttpContext context)
    {
        var authentication = await context.AuthenticateAsync(GoogleAuthPolicy.CookieScheme).ConfigureAwait(false);
        var authTime = context.User.FindFirstValue("auth_time")
            ?? authentication.Properties?.IssuedUtc?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(
            new BrowserUserResponse(
                context.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
                context.User.FindFirstValue(ClaimTypes.Name),
                authTime),
            AcJsonContext.Default.BrowserUserResponse,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> EnrollAsync(
        HttpContext context,
        EnrollmentService service,
        ServerDb db,
        CancellationToken ct)
    {
        var request = await JsonSerializer.DeserializeAsync(context.Request.Body, AcJsonContext.Default.EnrollmentRequest, ct)
            .ConfigureAwait(false);
        if (request is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Request body is required.");
        }

        var ip = context.Items[GeoGateMiddleware.IpItemKey] as string ?? context.Connection.RemoteIpAddress?.ToString();
        var result = await service.EnrollAsync(request, ip, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            var status = result.ErrorCode == AcErrorCodes.EnrollmentExpired ? StatusCodes.Status410Gone : StatusCodes.Status400BadRequest;
            return Problem(context, status, result.ErrorCode ?? AcErrorCodes.InvalidCode, "Enrollment failed.");
        }

        await db.AddAuditAsync(new AuditLogEntry("enrollment-code", ip, null, "enrollment.success", result.MachineId, null, "success"), ct)
            .ConfigureAwait(false);
        return Results.Json(
            new EnrollmentResponse(new MachineId(result.MachineId!)),
            AcJsonContext.Default.EnrollmentResponse,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> ChallengeAsync(
        HttpContext context,
        AgentAuthService service,
        TimeProvider time,
        CancellationToken ct)
    {
        var request = await JsonSerializer.DeserializeAsync(context.Request.Body, AcJsonContext.Default.AgentChallengeRequest, ct)
            .ConfigureAwait(false);
        if (request is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Request body is required.");
        }

        var result = await service.ChallengeAsync(request.MachineId, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, result.ErrorCode ?? AcErrorCodes.Unauthorized, "Challenge rejected.");
        }

        var expiresIn = Math.Max(1, (int)Math.Ceiling((result.ExpiresAt - time.GetUtcNow()).TotalSeconds));
        return Results.Json(
            new AgentChallengeResponse(result.Value!, expiresIn),
            AcJsonContext.Default.AgentChallengeResponse,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> TokenAsync(
        HttpContext context,
        AgentAuthService service,
        CancellationToken ct)
    {
        var request = await JsonSerializer.DeserializeAsync(context.Request.Body, AcJsonContext.Default.AgentTokenRequest, ct)
            .ConfigureAwait(false);
        if (request is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Request body is required.");
        }

        var result = await service.IssueTokenAsync(request, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, result.ErrorCode ?? AcErrorCodes.Unauthorized, "Token rejected.");
        }

        return Results.Json(
            new AgentTokenResponse(result.Value!, AgentTokenDefaults.LifetimeSeconds),
            AcJsonContext.Default.AgentTokenResponse,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> CreateEnrollmentCodeAsync(
        HttpContext context,
        EnrollmentService service,
        ServerDb db,
        CancellationToken ct)
    {
        var actor = context.User.FindFirstValue(ClaimTypes.Email)
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "user";
        var code = await service.CreateCodeAsync(ct).ConfigureAwait(false);
        await db.AddAuditAsync(new AuditLogEntry(actor, context.Connection.RemoteIpAddress?.ToString(), null, "enrollment.code.create", null, null, "success"), ct)
            .ConfigureAwait(false);
        return Results.Json(code, AcJsonContext.Default.EnrollmentCodeResponse, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> RevokeMachineAsync(
        string machineId,
        ServerDb db,
        CancellationToken ct)
    {
        await db.RevokeMachineAsync(machineId, ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMachinesAsync(
        HttpContext context,
        ServerDb db,
        ConnectionRegistry connections,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var machines = await db.GetMachinesAsync(ct).ConfigureAwait(false);
        var vpnStates = await db
            .GetVpnStatesAsync(machines.Select(machine => machine.Id).ToArray(), ct)
            .ConfigureAwait(false);
        var views = machines
            .Where(machine => machine.RevokedAt is null)
            .Select(machine => ToMachineInfo(
                machine,
                connections,
                vpnStates.GetValueOrDefault(machine.Id)))
            .ToList();
        return Results.Json(
            new ApiResponse<List<MachineInfo>>(
                views,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), views.Any(x => x.Online), false, 0, traceId)),
            AcJsonContext.Default.ApiResponseListMachineInfo,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetMachineAsync(
        HttpContext context,
        string machineId,
        ServerDb db,
        ConnectionRegistry connections,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var machine = await db.GetMachineAsync(machineId, ct).ConfigureAwait(false);
        if (machine is null || machine.RevokedAt is not null)
        {
            return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.MachineNotFound, $"Machine '{machineId}' was not found.");
        }

        var vpn = await db.GetVpnStateAsync(machineId, ct).ConfigureAwait(false);
        var view = ToMachineInfo(machine, connections, vpn);
        return Results.Json(
            new ApiResponse<MachineInfo>(
                view,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), view.Online, false, 0, traceId)),
            AcJsonContext.Default.ApiResponseMachineInfo,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetInstancesAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var query = new AgentInstancesQuery(context.Request.Query["kind"].FirstOrDefault());
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.AgentInstancesQuery);
        return await QueryAsync(context, machineId, QueryTypes.AgentInstances, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var instance = context.Request.Query["instance"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(instance))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var includeChildren = !bool.TryParse(context.Request.Query["includeChildren"], out var include) || include;
        var query = new SessionListQuery(
            instance,
            context.Request.Query["view"].FirstOrDefault() ?? SessionListViews.Active,
            includeChildren,
            context.Request.Query["cursor"].FirstOrDefault());
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.SessionListQuery);
        return await QueryAsync(context, machineId, QueryTypes.SessionList, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var session = SessionFrom(context, machineId, sessionId);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var query = new SessionGetQuery(session.Value);
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.SessionGetQuery);
        return await QueryAsync(context, machineId, QueryTypes.SessionGet, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetMessagesAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var session = SessionFrom(context, machineId, sessionId);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var limit = int.TryParse(context.Request.Query["limit"], CultureInfo.InvariantCulture, out var parsed) ? parsed : 50;
        var query = new SessionMessagesQuery(
            session.Value,
            context.Request.Query["cursor"].FirstOrDefault(),
            Math.Clamp(limit, 1, 200));
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.SessionMessagesQuery);
        return await QueryAsync(context, machineId, QueryTypes.SessionMessages, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetStreamAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        SessionStreamHub streams,
        SessionWatchCoordinator watches,
        StreamOptions streamOptions,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var cursor = context.Request.Query["cursor"].FirstOrDefault();
        await watches.TouchRestAsync(machineId, sessionId, ct).ConfigureAwait(false);
        var waitMs = int.TryParse(
            context.Request.Query["waitMs"].FirstOrDefault(),
            CultureInfo.InvariantCulture,
            out var parsedWait)
            ? parsedWait
            : 0;
        var maxWait = (int)streamOptions.LongPollMax.TotalMilliseconds;
        var result = await streams.ReadAsync(
                machineId,
                sessionId,
                cursor,
                TimeSpan.FromMilliseconds(Math.Clamp(waitMs, 0, maxWait)),
                ct)
            .ConfigureAwait(false);
        var view = new StreamReadView(
            sessionId,
            result.Epoch,
            result.NextCursor,
            result.CursorExpired,
            result.Chunks);
        return Results.Json(
            new ApiResponse<StreamReadView>(
                view,
                new ResponseMeta(
                    MetaSources.Live,
                    time.GetUtcNow(),
                    false,
                    result.CursorExpired,
                    0,
                    traceId)),
            AcJsonContext.Default.ApiResponseStreamReadView,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetModelsAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var query = new SessionModelsQuery(context.Request.Query["instance"].FirstOrDefault());
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.SessionModelsQuery);
        return await QueryAsync(context, machineId, QueryTypes.SessionModels, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetFeaturesAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct) =>
        await QueryAsync(context, machineId, QueryTypes.MachineFeatures, EmptyObject, queries, ct).ConfigureAwait(false);

    private static async Task<IResult> GetVpnPageAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var query = new VpnPageQuery(
            context.Request.Query["country"].FirstOrDefault(),
            context.Request.Query["prevCity"].FirstOrDefault());
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.VpnPageQuery);
        return await QueryAsync(context, machineId, QueryTypes.VpnPage, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetVpnStatusAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct) =>
        await QueryAsync(context, machineId, QueryTypes.VpnStatus, EmptyObject, queries, ct).ConfigureAwait(false);

    private static async Task<IResult> GetVpnCountriesAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct) =>
        await QueryAsync(context, machineId, QueryTypes.VpnCountries, EmptyObject, queries, ct).ConfigureAwait(false);

    private static async Task<IResult> GetVpnServersAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var country = context.Request.Query["country"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(country))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'country' query parameter is required.");
        }

        var refresh = bool.TryParse(context.Request.Query["refresh"], out var parsed) && parsed;
        var query = new VpnServersQuery(
            country,
            context.Request.Query["prevCity"].FirstOrDefault(),
            refresh);
        var payload = JsonSerializer.SerializeToElement(query, AcJsonContext.Default.VpnServersQuery);
        return await QueryAsync(context, machineId, QueryTypes.VpnServers, payload, queries, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> ConnectVpnAsync(
        HttpContext context,
        string machineId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(context, AcJsonContext.Default.VpnConnectRequest, ct).ConfigureAwait(false)
            ?? new VpnConnectRequest(null, null, null);
        var payload = JsonSerializer.SerializeToElement(
            new VpnConnectPayload(body.Server, body.Country, body.City, body.Force),
            AcJsonContext.Default.VpnConnectPayload);
        return await IssueCommandAsync(context, machineId, CommandTypes.VpnConnect, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> DisconnectVpnAsync(
        HttpContext context,
        string machineId,
        CommandExecutionService commands,
        CancellationToken ct) =>
        await IssueCommandAsync(context, machineId, CommandTypes.VpnDisconnect, EmptyObject, commands, ct)
            .ConfigureAwait(false);

    private static async Task<IResult> RefreshInstancesAsync(
        HttpContext context,
        string machineId,
        CommandExecutionService commands,
        CancellationToken ct) =>
        await IssueCommandAsync(context, machineId, CommandTypes.InstanceRefresh, EmptyObject, commands, ct)
            .ConfigureAwait(false);

    private static async Task<IResult> AbortSessionAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var session = SessionFrom(context, machineId, sessionId);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var body = await ReadBodyAsync(context, AcJsonContext.Default.AbortSessionBody, ct).ConfigureAwait(false)
            ?? new AbortSessionBody();
        var payload = JsonSerializer.SerializeToElement(
            new SessionAbortPayload(session.Value, body.IncludeChildren),
            AcJsonContext.Default.SessionAbortPayload);
        return await IssueCommandAsync(context, machineId, CommandTypes.SessionAbort, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> PromptSessionAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var session = SessionFrom(context, machineId, sessionId);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var body = await ReadBodyAsync(context, AcJsonContext.Default.PromptBody, ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Text))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "A non-empty prompt is required.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new SessionPromptPayload(session.Value, body.Text, body.Model, body.Agent),
            AcJsonContext.Default.SessionPromptPayload);
        return await IssueCommandAsync(context, machineId, CommandTypes.SessionPrompt, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> AbortAndPromptSessionAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var session = SessionFrom(context, machineId, sessionId);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var body = await ReadBodyAsync(context, AcJsonContext.Default.AbortAndPromptBody, ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Text))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "A non-empty prompt is required.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new SessionAbortAndPromptPayload(session.Value, body.Text, body.Model),
            AcJsonContext.Default.SessionAbortAndPromptPayload);
        return await IssueCommandAsync(context, machineId, CommandTypes.SessionAbortAndPrompt, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ReplyPermissionAsync(
        HttpContext context,
        string machineId,
        string sessionId,
        string requestId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var session = SessionFrom(context, machineId, sessionId);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The 'instance' query parameter is required.");
        }

        var body = await ReadBodyAsync(context, AcJsonContext.Default.PermissionReplyBody, ct).ConfigureAwait(false);
        if (body is null || body.Response is not (PermissionResponses.Once or PermissionResponses.Always or PermissionResponses.Reject))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "The permission response must be 'once', 'always', or 'reject'.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new SessionPermissionReplyPayload(session.Value, requestId, body.Response),
            AcJsonContext.Default.SessionPermissionReplyPayload);
        return await IssueCommandAsync(context, machineId, CommandTypes.SessionPermissionReply, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ListCommandsAsync(
        HttpContext context,
        ServerDb db,
        TimeProvider time,
        CancellationToken ct)
    {
        var machineId = context.Request.Query["machineId"].FirstOrDefault();
        CommandStatus? status = Enum.TryParse<CommandStatus>(context.Request.Query["status"], true, out var parsed)
            ? parsed
            : null;
        var limit = int.TryParse(context.Request.Query["limit"], CultureInfo.InvariantCulture, out var parsedLimit)
            ? parsedLimit
            : 50;
        var commands = await db.ListCommandsAsync(machineId, status, limit, ct).ConfigureAwait(false);
        return Results.Json(
            new ApiResponse<List<CommandDetailView>>(
                commands,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), false, false, 0, TraceId(context))),
            AcJsonContext.Default.ApiResponseListCommandDetailView,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetCommandAsync(
        HttpContext context,
        string commandId,
        ServerDb db,
        TimeProvider time,
        CancellationToken ct)
    {
        var command = await db.GetCommandAsync(commandId, ct).ConfigureAwait(false);
        if (command is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.CommandNotFound, $"Command '{commandId}' was not found.");
        }

        context.Response.Headers["X-Request-Id"] = command.CommandId;
        return Results.Json(
            new ApiResponse<CommandDetailView>(
                command,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), false, false, 0, TraceId(context))),
            AcJsonContext.Default.ApiResponseCommandDetailView,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListAuditAsync(
        HttpContext context,
        ServerDb db,
        TimeProvider time,
        CancellationToken ct)
    {
        var from = DateTimeOffset.TryParse(context.Request.Query["from"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedFrom)
            ? parsedFrom
            : null as DateTimeOffset?;
        var to = DateTimeOffset.TryParse(context.Request.Query["to"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTo)
            ? parsedTo
            : null as DateTimeOffset?;
        var limit = int.TryParse(context.Request.Query["limit"], CultureInfo.InvariantCulture, out var parsedLimit)
            ? parsedLimit
            : 100;
        var entries = await db.ListAuditAsync(from, to, limit, ct).ConfigureAwait(false);
        return Results.Json(
            new ApiResponse<List<AuditLogView>>(
                entries,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), false, false, 0, TraceId(context))),
            AcJsonContext.Default.ApiResponseListAuditLogView,
            statusCode: StatusCodes.Status200OK);
    }

    private static IResult ListServerLogsAsync(HttpContext context, TimeProvider time)
    {
        var directory = ServerLogDirectory();
        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.txt")
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new LogFileView(info.Name, info.Length, info.LastWriteTimeUtc);
                })
                .OrderByDescending(file => file.ModifiedAt)
                .ToList()
            : [];
        return Results.Json(
            new ApiResponse<List<LogFileView>>(
                files,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), false, false, 0, TraceId(context))),
            AcJsonContext.Default.ApiResponseListLogFileView,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> ReadServerLogAsync(
        HttpContext context,
        string name,
        TimeProvider time,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Invalid log file name.");
        }

        var path = Path.Combine(ServerLogDirectory(), name);
        if (!File.Exists(path))
        {
            return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, $"Log file '{name}' was not found.");
        }

        var limit = int.TryParse(context.Request.Query["lines"], CultureInfo.InvariantCulture, out var parsed) ? parsed : 500;
        var (content, truncated) = await ReadTailAsync(path, Math.Clamp(limit, 1, 5000), ct).ConfigureAwait(false);
        return Results.Json(
            new ApiResponse<LogTextView>(
                new LogTextView(name, content, truncated),
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), false, false, 0, TraceId(context))),
            AcJsonContext.Default.ApiResponseLogTextView,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> QueryAsync(
        HttpContext context,
        string machineId,
        string type,
        JsonElement payload,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var actor = UserId(context);
        var result = await queries
            .QueryAsync(machineId, type, payload, CacheKey(type, payload), actor, traceId, ct)
            .ConfigureAwait(false);
        if (!result.Success || result.Meta is null)
        {
            return Problem(
                context,
                result.StatusCode,
                result.ErrorCode ?? AcErrorCodes.QueryFailed,
                result.Detail ?? $"Query '{type}' failed.");
        }

        return Results.Json(
            new ApiResponse<JsonElement>(result.Data ?? NullElement, result.Meta),
            AcJsonContext.Default.ApiResponseJsonElement,
            statusCode: result.StatusCode);
    }

    private static async Task<IResult> IssueCommandAsync(
        HttpContext context,
        string machineId,
        string type,
        JsonElement payload,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var actor = UserId(context);
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(idempotencyKey) && idempotencyKey.Length > 128)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Idempotency-Key must be at most 128 characters.");
        }

        var requestedId = context.Request.Headers["X-Request-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(requestedId) && !RequestIds.IsValid(requestedId))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "X-Request-Id must be a valid ULID.");
        }

        TimeSpan? wait = null;
        if (double.TryParse(context.Request.Query["wait"], CultureInfo.InvariantCulture, out var waitSeconds))
        {
            wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 120));
        }

        var ip = context.Items[GeoGateMiddleware.IpItemKey] as string
            ?? context.Connection.RemoteIpAddress?.ToString();
        var country = context.Items[GeoGateMiddleware.CountryItemKey] as string;
        var result = await commands
            .ExecuteAsync(
                machineId,
                type,
                payload,
                userId,
                actor,
                idempotencyKey,
                requestedId,
                traceId,
                ip,
                country,
                wait,
                ct)
            .ConfigureAwait(false);

        if (result.Command is not null)
        {
            context.Response.Headers["X-Request-Id"] = result.Command.CommandId;
            return Results.Json(
                new ApiResponse<CommandDetailView>(
                    result.Command,
                    new ResponseMeta(MetaSources.Live, result.Command.UpdatedAt, true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseCommandDetailView,
                statusCode: result.StatusCode);
        }

        if (result.Pending is not null)
        {
            context.Response.Headers["X-Request-Id"] = result.Pending.RequestId;
            return Results.Json(
                new ApiResponse<CommandPendingView>(
                    result.Pending,
                    new ResponseMeta(
                        MetaSources.Live,
                        context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(),
                        true,
                        false,
                        0,
                        traceId)),
                AcJsonContext.Default.ApiResponseCommandPendingView,
                statusCode: result.StatusCode);
        }

        return Problem(
            context,
            result.StatusCode,
            result.ErrorCode ?? AcErrorCodes.InternalError,
            result.Detail ?? $"Command '{type}' failed.");
    }

    private static async Task<T?> ReadBodyAsync<T>(
        HttpContext context,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        if (context.Request.ContentLength is null or 0
            && string.IsNullOrEmpty(context.Request.ContentType))
        {
            return default;
        }

        return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, ct).ConfigureAwait(false);
    }

    private static SessionRef? SessionFrom(HttpContext context, string machineId, string sessionId)
    {
        var instance = context.Request.Query["instance"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(instance)
            ? null
            : new SessionRef(new MachineId(machineId), new InstanceId(instance), sessionId);
    }

    private static string UserId(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.Email)
        ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "user";

    private static string TraceId(HttpContext context)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-Id"] = traceId;
        return traceId;
    }

    private static string CacheKey(string type, JsonElement payload) => type + ":" + payload.GetRawText();

    private static MachineInfo ToMachineInfo(
        MachineEntity machine,
        ConnectionRegistry connections,
        VpnStatusView? vpn = null) =>
        MachineViewFactory.Create(machine, connections, vpn);

    private static string ServerLogDirectory() =>
        Path.GetDirectoryName(AcLog.FilePath) ?? Path.Combine(AppContext.BaseDirectory, "logs");

    private static async Task<(string Content, bool Truncated)> ReadTailAsync(
        string path,
        int lines,
        CancellationToken ct)
    {
        const int maxBytes = 512 * 1024;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var all = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var offset = start > 0 ? 1 : 0;
        var available = all.Length - offset;
        var truncated = start > 0 || available > lines;
        var selected = all.Skip(offset).TakeLast(Math.Min(lines, Math.Max(0, available)));
        return (string.Join(Environment.NewLine, selected), truncated);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    private static IResult Problem(HttpContext context, int status, string code, string detail) =>
        Results.Json(
            new AcProblemDetails("about:blank", status switch
            {
                StatusCodes.Status400BadRequest => "Bad Request",
                StatusCodes.Status401Unauthorized => "Unauthorized",
                StatusCodes.Status403Forbidden => "Forbidden",
                StatusCodes.Status404NotFound => "Not Found",
                StatusCodes.Status409Conflict => "Conflict",
                StatusCodes.Status502BadGateway => "Bad Gateway",
                StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
                StatusCodes.Status504GatewayTimeout => "Gateway Timeout",
                _ => "Request Failed",
            }, status, detail, context.Request.Path, code),
            AcJsonContext.Default.AcProblemDetails,
            statusCode: status,
            contentType: "application/problem+json");
}
