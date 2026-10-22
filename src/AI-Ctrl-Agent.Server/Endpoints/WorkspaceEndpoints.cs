using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Requests;
using AiCtrl.Server.Security;
using AiCtrl.Server.Transfers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiCtrl.Server.Endpoints;

public static class WorkspaceEndpoints
{
    public static void MapAcWorkspaceEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/machines/{machineId}/workspaces", ListWorkspacesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/workspaces/{workspaceId}/files", ListFilesAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/workspaces/{workspaceId}/files/stat", StatFileAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/workspaces/{workspaceId}/files/preview", PreviewFileAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/workspaces/{workspaceId}/files/mkdir", MkdirAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/workspaces/{workspaceId}/files/delete", DeleteAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/workspaces/{workspaceId}/files/rename", RenameAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/workspaces/{workspaceId}/transfers/uploads", CreateUploadAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/workspaces/{workspaceId}/transfers/downloads", CreateDownloadAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/transfers/{transferId}", GetTransferAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPut("/api/v1/machines/{machineId}/transfers/{transferId}/chunks/{index}", PutChunkAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/transfers/{transferId}/complete", CompleteTransferAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/machines/{machineId}/transfers/{transferId}/dispatch", DispatchTransferAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapGet("/api/v1/machines/{machineId}/transfers/{transferId}/content", GetContentAsync)
            .RequireAuthorization(GoogleAuthPolicy.BrowserPolicy);

        app.MapPost("/api/v1/agent/transfers/{transferId}/begin", AgentBeginAsync)
            .RequireAuthorization(AgentTokenDefaults.Policy);

        app.MapPut("/api/v1/agent/transfers/{transferId}/chunks/{index}", AgentPutChunkAsync)
            .RequireAuthorization(AgentTokenDefaults.Policy);

        app.MapPost("/api/v1/agent/transfers/{transferId}/complete", AgentCompleteAsync)
            .RequireAuthorization(AgentTokenDefaults.Policy);

        app.MapGet("/api/v1/agent/transfers/{transferId}/content", AgentGetContentAsync)
            .RequireAuthorization(AgentTokenDefaults.Policy);

        app.MapGet("/api/v1/agent/transfers/{transferId}", AgentGetTransferAsync)
            .RequireAuthorization(AgentTokenDefaults.Policy);

        app.MapPost("/api/v1/agent/transfers/{transferId}/release", AgentReleaseAsync)
            .RequireAuthorization(AgentTokenDefaults.Policy);
    }

    private static async Task<IResult> ListWorkspacesAsync(
        HttpContext context,
        string machineId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();
        return await QueryAsync(context, machineId, QueryTypes.WorkspaceList, payload, queries, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ListFilesAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var query = context.Request.Query;
        var payload = JsonSerializer.SerializeToElement(
            new FileListQuery(
                workspaceId,
                query["relPath"].FirstOrDefault() ?? string.Empty,
                query["sort"].FirstOrDefault() ?? FileSorts.Name,
                query["cursor"].FirstOrDefault(),
                ParseInt(query["limit"], 200)),
            AcJsonContext.Default.FileListQuery);
        return await QueryAsync(context, machineId, QueryTypes.FilesList, payload, queries, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> StatFileAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var relPath = context.Request.Query["relPath"].FirstOrDefault() ?? string.Empty;
        var payload = JsonSerializer.SerializeToElement(
            new FileStatQuery(workspaceId, relPath, context.Request.Query["hash"].FirstOrDefault() is "1" or "true"),
            AcJsonContext.Default.FileStatQuery);
        return await QueryAsync(context, machineId, QueryTypes.FilesStat, payload, queries, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> PreviewFileAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        RemoteQueryService queries,
        CancellationToken ct)
    {
        var relPath = context.Request.Query["relPath"].FirstOrDefault() ?? string.Empty;
        var payload = JsonSerializer.SerializeToElement(
            new FilePreviewQuery(workspaceId, relPath),
            AcJsonContext.Default.FilePreviewQuery);
        return await QueryAsync(context, machineId, QueryTypes.FilesPreview, payload, queries, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> MkdirAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(context, AcJsonContext.Default.FilesMkdirPayload, ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.RelPath))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "relPath is required.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new FilesMkdirPayload(workspaceId, body.RelPath),
            AcJsonContext.Default.FilesMkdirPayload);
        return await IssueAsync(context, machineId, CommandTypes.FilesMkdir, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(context, AcJsonContext.Default.FilesDeletePayload, ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.RelPath))
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "relPath is required.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new FilesDeletePayload(workspaceId, body.RelPath),
            AcJsonContext.Default.FilesDeletePayload);
        return await IssueAsync(context, machineId, CommandTypes.FilesDelete, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> RenameAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(context, AcJsonContext.Default.FilesRenamePayload, ct).ConfigureAwait(false);
        if (body is null
            || string.IsNullOrWhiteSpace(body.RelPath)
            || string.IsNullOrWhiteSpace(body.NewName))
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                AcErrorCodes.InvalidPayload,
                "relPath and newName are required.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new FilesRenamePayload(workspaceId, body.RelPath, body.NewName),
            AcJsonContext.Default.FilesRenamePayload);
        return await IssueAsync(context, machineId, CommandTypes.FilesRename, payload, commands, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> CreateUploadAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var body = await ReadBodyAsync(context, AcJsonContext.Default.UploadCreateRequest, ct).ConfigureAwait(false);
        if (body is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Upload body is required.");
        }

        try
        {
            var created = await transfers.CreateUploadAsync(machineId, workspaceId, body, ct).ConfigureAwait(false);
            return Results.Json(
                new ApiResponse<TransferCreatedView>(
                    created,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferCreatedView,
                statusCode: StatusCodes.Status201Created);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> CreateDownloadAsync(
        HttpContext context,
        string machineId,
        string workspaceId,
        TransferStore transfers,
        CommandExecutionService commands,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var body = await ReadBodyAsync(context, AcJsonContext.Default.DownloadCreateRequest, ct).ConfigureAwait(false);
        if (body is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Download body is required.");
        }

        TransferCreatedView created;
        try
        {
            created = await transfers.CreateDownloadAsync(machineId, workspaceId, body, ct).ConfigureAwait(false);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }

        await DispatchAsync(
            context,
            machineId,
            created.TransferId,
            workspaceId,
            body.RelPath,
            body.AsZip,
            commands,
            ct).ConfigureAwait(false);

        return Results.Json(
            new ApiResponse<TransferCreatedView>(
                created,
                new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
            AcJsonContext.Default.ApiResponseTransferCreatedView,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetTransferAsync(
        HttpContext context,
        string machineId,
        string transferId,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var status = await transfers.GetStatusAsync(transferId, ct).ConfigureAwait(false);
            if (status.MachineId != machineId)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> PutChunkAsync(
        HttpContext context,
        string machineId,
        string transferId,
        int index,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var entity = await transfers.RequireAsync(transferId, ct).ConfigureAwait(false);
            if (entity.MachineId != machineId)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            if (context.Request.ContentLength is long length && length > entity.ChunkSize)
            {
                return Problem(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    AcErrorCodes.InvalidPayload,
                    $"Chunk must be at most {entity.ChunkSize} bytes.");
            }

            var status = await transfers.WriteChunkAsync(transferId, index, context.Request.Body, ct)
                .ConfigureAwait(false);
            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> CompleteTransferAsync(
        HttpContext context,
        string machineId,
        string transferId,
        TransferStore transfers,
        CommandExecutionService commands,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var entity = await transfers.RequireAsync(transferId, ct).ConfigureAwait(false);
            if (entity.MachineId != machineId)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            var status = await transfers.CompleteAsync(transferId, ct).ConfigureAwait(false);
            if (status.Direction == TransferDirections.Upload)
            {
                await DispatchReceiveAsync(context, machineId, status, commands, ct).ConfigureAwait(false);
            }

            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> DispatchTransferAsync(
        HttpContext context,
        string machineId,
        string transferId,
        TransferStore transfers,
        CommandExecutionService commands,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var status = await transfers.GetStatusAsync(transferId, ct).ConfigureAwait(false);
            if (status.MachineId != machineId)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            if (status.Status != TransferStatuses.Ready)
            {
                return Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    $"Transfer is {status.Status}.");
            }

            var result = status.Direction == TransferDirections.Upload
                ? await DispatchReceiveAsync(context, machineId, status, commands, ct).ConfigureAwait(false)
                : await DispatchSendAsync(context, machineId, status, commands, ct).ConfigureAwait(false);
            if (result is null)
            {
                return Problem(context, StatusCodes.Status502BadGateway, AcErrorCodes.InternalError, "Dispatch failed.");
            }

            return Results.Json(
                new ApiResponse<TransferCommandView>(
                    result,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferCommandView,
                statusCode: result.Pending ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> GetContentAsync(
        HttpContext context,
        string machineId,
        string transferId,
        TransferStore transfers,
        CancellationToken ct)
    {
        try
        {
            var entity = await transfers.RequireAsync(transferId, ct).ConfigureAwait(false);
            if (entity.MachineId != machineId)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            if (entity.Status != TransferStatuses.Ready)
            {
                return Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    $"Transfer is {entity.Status}.");
            }

            return Results.File(
                transfers.DataPath(transferId),
                "application/octet-stream",
                entity.FileName,
                enableRangeProcessing: true);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> AgentBeginAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var body = await ReadBodyAsync(context, AcJsonContext.Default.TransferBeginRequest, ct).ConfigureAwait(false);
        if (body is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, AcErrorCodes.InvalidPayload, "Begin body is required.");
        }

        try
        {
            var owned = await OwnAsync(context, transferId, transfers, ct).ConfigureAwait(false);
            if (!owned)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            var status = await transfers.BeginDownloadAsync(transferId, body.Size, body.Sha256, ct)
                .ConfigureAwait(false);
            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> AgentPutChunkAsync(
        HttpContext context,
        string transferId,
        int index,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var entity = await OwnEntityAsync(context, transferId, transfers, ct).ConfigureAwait(false);
            if (entity is null)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            if (entity.Direction != TransferDirections.Download)
            {
                return Problem(context, StatusCodes.Status409Conflict, AcErrorCodes.InvalidPayload, "Transfer is not a download.");
            }

            if (context.Request.ContentLength is long length && length > entity.ChunkSize)
            {
                return Problem(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    AcErrorCodes.InvalidPayload,
                    $"Chunk must be at most {entity.ChunkSize} bytes.");
            }

            var status = await transfers.WriteChunkAsync(transferId, index, context.Request.Body, ct)
                .ConfigureAwait(false);
            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> AgentCompleteAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var entity = await transfers.RequireAsync(transferId, ct).ConfigureAwait(false);
            if (!string.Equals(entity.MachineId, MachineId(context), StringComparison.Ordinal))
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            var status = await transfers.CompleteAsync(transferId, ct).ConfigureAwait(false);
            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> AgentGetContentAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        CancellationToken ct)
    {
        try
        {
            var entity = await OwnEntityAsync(context, transferId, transfers, ct).ConfigureAwait(false);
            if (entity is null)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            if (entity.Direction != TransferDirections.Upload)
            {
                return Problem(context, StatusCodes.Status409Conflict, AcErrorCodes.InvalidPayload, "Transfer is not an upload.");
            }

            if (entity.Status != TransferStatuses.Ready)
            {
                return Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    $"Transfer is {entity.Status}.");
            }

            return Results.File(
                transfers.DataPath(transferId),
                "application/octet-stream",
                entity.FileName,
                enableRangeProcessing: true);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> AgentGetTransferAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        TimeProvider time,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        try
        {
            var entity = await OwnEntityAsync(context, transferId, transfers, ct).ConfigureAwait(false);
            if (entity is null)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            var status = await transfers.GetStatusAsync(transferId, ct).ConfigureAwait(false);
            return Results.Json(
                new ApiResponse<TransferStatusView>(
                    status,
                    new ResponseMeta(MetaSources.Live, time.GetUtcNow(), true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseTransferStatusView,
                statusCode: StatusCodes.Status200OK);
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> AgentReleaseAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        CancellationToken ct)
    {
        try
        {
            var owned = await OwnAsync(context, transferId, transfers, ct).ConfigureAwait(false);
            if (!owned)
            {
                return Problem(context, StatusCodes.Status404NotFound, AcErrorCodes.InvalidPayload, "Transfer was not found.");
            }

            await transfers.ReleaseAsync(transferId, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (TransferException ex)
        {
            return Problem(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<bool> OwnAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        CancellationToken ct)
    {
        var entity = await OwnEntityAsync(context, transferId, transfers, ct).ConfigureAwait(false);
        return entity is not null;
    }

    private static async Task<AiCtrl.Server.Persistence.TransferEntity?> OwnEntityAsync(
        HttpContext context,
        string transferId,
        TransferStore transfers,
        CancellationToken ct)
    {
        var entity = await transfers.RequireAsync(transferId, ct).ConfigureAwait(false);
        return string.Equals(entity.MachineId, MachineId(context), StringComparison.Ordinal) ? entity : null;
    }

    private static async Task DispatchAsync(
        HttpContext context,
        string machineId,
        string transferId,
        string workspaceId,
        string relPath,
        bool asZip,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new FilesSendPayload(transferId, workspaceId, relPath, asZip),
            AcJsonContext.Default.FilesSendPayload);
        await ExecuteAsync(context, machineId, CommandTypes.FilesSend, payload, commands, TimeSpan.FromSeconds(1), ct)
            .ConfigureAwait(false);
    }

    private static async Task<TransferCommandView?> DispatchReceiveAsync(
        HttpContext context,
        string machineId,
        TransferStatusView status,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var name = status.RelPath.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        var relDir = slash < 0 ? string.Empty : name[..slash];
        var fileName = slash < 0 ? name : name[(slash + 1)..];
        var payload = JsonSerializer.SerializeToElement(
            new FilesReceivePayload(
                status.TransferId,
                status.WorkspaceId,
                relDir,
                fileName,
                status.Conflict ?? TransferConflicts.Fail,
                status.Sha256),
            AcJsonContext.Default.FilesReceivePayload);
        return await ExecuteAsync(
            context,
            machineId,
            CommandTypes.FilesReceive,
            payload,
            commands,
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(false);
    }

    private static async Task<TransferCommandView?> DispatchSendAsync(
        HttpContext context,
        string machineId,
        TransferStatusView status,
        CommandExecutionService commands,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new FilesSendPayload(status.TransferId, status.WorkspaceId, status.RelPath, status.AsZip),
            AcJsonContext.Default.FilesSendPayload);
        return await ExecuteAsync(
            context,
            machineId,
            CommandTypes.FilesSend,
            payload,
            commands,
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(false);
    }

    private static async Task<TransferCommandView?> ExecuteAsync(
        HttpContext context,
        string machineId,
        string type,
        JsonElement payload,
        CommandExecutionService commands,
        TimeSpan wait,
        CancellationToken ct)
    {
        var traceId = TraceId(context);
        var actor = UserId(context);
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor;
        var result = await commands.ExecuteAsync(
            machineId,
            type,
            payload,
            userId,
            actor,
            null,
            null,
            traceId,
            context.Connection.RemoteIpAddress?.ToString(),
            null,
            wait,
            ct).ConfigureAwait(false);
        if (result.Command is not null)
        {
            return new TransferCommandView(
                payload.TryGetProperty("transferId", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                result.Command.CommandId,
                result.Command.Status.ToString(),
                false);
        }

        if (result.Pending is not null)
        {
            return new TransferCommandView(
                payload.TryGetProperty("transferId", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                result.Pending.RequestId,
                result.Pending.Status,
                true);
        }

        return null;
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
        var result = await queries.QueryAsync(machineId, type, payload, type + ":" + payload.GetRawText(), actor, traceId, ct)
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
            new ApiResponse<JsonElement>(result.Data ?? default, result.Meta),
            AcJsonContext.Default.ApiResponseJsonElement,
            statusCode: result.StatusCode);
    }

    private static async Task<IResult> IssueAsync(
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
        var result = await commands.ExecuteAsync(
            machineId,
            type,
            payload,
            userId,
            actor,
            null,
            null,
            traceId,
            context.Connection.RemoteIpAddress?.ToString(),
            null,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);
        if (result.Command is not null)
        {
            return Results.Json(
                new ApiResponse<CommandDetailView>(
                    result.Command,
                    new ResponseMeta(MetaSources.Live, result.Command.UpdatedAt, true, false, 0, traceId)),
                AcJsonContext.Default.ApiResponseCommandDetailView,
                statusCode: result.StatusCode);
        }

        if (result.Pending is not null)
        {
            return Results.Json(
                new ApiResponse<CommandPendingView>(
                    result.Pending,
                    new ResponseMeta(MetaSources.Live, context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), true, false, 0, traceId)),
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

    private static string MachineId(HttpContext context) =>
        context.User.FindFirstValue(AgentTokenDefaults.MachineIdClaim) ?? string.Empty;

    private static string UserId(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.Email)
        ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "user";

    private static string TraceId(HttpContext context)
    {
        var traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-Id"] = traceId;
        return traceId;
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static IResult Problem(HttpContext context, int status, string code, string detail) =>
        Results.Json(
            new AcProblemDetails(
                "about:blank",
                status switch
                {
                    StatusCodes.Status400BadRequest => "Bad Request",
                    StatusCodes.Status401Unauthorized => "Unauthorized",
                    StatusCodes.Status403Forbidden => "Forbidden",
                    StatusCodes.Status404NotFound => "Not Found",
                    StatusCodes.Status409Conflict => "Conflict",
                    StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
                    StatusCodes.Status416RangeNotSatisfiable => "Range Not Satisfiable",
                    StatusCodes.Status422UnprocessableEntity => "Unprocessable Entity",
                    StatusCodes.Status507InsufficientStorage => "Insufficient Storage",
                    _ => "Request Failed",
                },
                status,
                detail,
                context.Request.Path,
                code),
            AcJsonContext.Default.AcProblemDetails,
            statusCode: status);
}
