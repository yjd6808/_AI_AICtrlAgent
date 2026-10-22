using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AiCtrl.ApiClient;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiCtrl.ClientMCP;

[McpServerToolType]
public sealed class LocalTools
{
    private const int MessageSummaryChars = 200;

    private readonly LocalApiClient _client;

    public LocalTools(LocalApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    [McpServerTool(Name = "local_status", ReadOnly = true, Idempotent = true)]
    [Description("Reports the local client, agent backend, and server connection status without going through the server.")]
    public async Task<CallToolResult> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            var data = response.Data;
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{Freshness(response.Meta)}\nMachine {data.MachineId.Value} version {data.Version}: {data.InstanceCount} instance(s), {data.SessionCount} tracked session(s), server connected: {data.ServerConnected}.");

            return Result(summary, response, AcJsonContext.Default.ApiResponseLocalStatusDto);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_list_agent_instances", ReadOnly = true, Idempotent = true)]
    [Description("Lists coding agent instances discovered on this machine.")]
    public async Task<CallToolResult> ListAgentInstancesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.ListInstancesAsync(ct).ConfigureAwait(false);
            var lines = response.Data.Select(instance =>
                $"- {instance.Id.Value} ({instance.Kind}, healthy={instance.Healthy}, managed={instance.Managed}, version={instance.Version ?? "unknown"})");
            var summary = $"{Freshness(response.Meta)}\n{response.Data.Count} instance(s):\n" + string.Join("\n", lines);

            return Result(summary, response, AcJsonContext.Default.ApiResponseListInstanceInfo);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_list_sessions", ReadOnly = true, Idempotent = true)]
    [Description("Lists sessions for this machine or for one instance, with the projected display state.")]
    public async Task<CallToolResult> ListSessionsAsync(
        [Description("Instance id such as 'opencode:local'. Omit to list sessions across all instances.")] string? instanceId = null,
        [Description("View: 'active' (default) or 'all'.")] string view = "active",
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.ListSessionsAsync(instanceId, view, ct).ConfigureAwait(false);
            var lines = response.Data.Sessions.Select(session =>
                $"- {session.Ref.SessionId} [{session.DisplayState}] {session.Title} updated={session.UpdatedAt:O}");
            var summary = $"{Freshness(response.Meta)}\n{response.Data.Sessions.Count} session(s) in view '{view}':\n" + string.Join("\n", lines);

            return Result(summary, response, AcJsonContext.Default.ApiResponseSessionPageDto);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_get_session_messages", ReadOnly = true, Idempotent = true)]
    [Description("Reads recent messages of a session, newest page first as returned by the provider.")]
    public async Task<CallToolResult> GetSessionMessagesAsync(
        [Description("Session id.")] string sessionId,
        [Description("Instance id. Omit to locate the session automatically.")] string? instanceId = null,
        [Description("Maximum number of messages, 1-200. Defaults to 20.")] int limit = 20,
        [Description("Pagination cursor returned by a previous call.")] string? cursor = null,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetMessagesAsync(sessionId, instanceId, limit, cursor, ct).ConfigureAwait(false);
            var lines = response.Data.Messages.Select(message =>
            {
                var text = string.Join(
                    " ",
                    message.Parts.Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
                return $"- [{message.Role}] {message.MessageId}: {Truncate(text, MessageSummaryChars)}";
            });
            var summary = $"{Freshness(response.Meta)}\n{response.Data.Messages.Count} message(s) for {sessionId}:\n" + string.Join("\n", lines);

            return Result(summary, response, AcJsonContext.Default.ApiResponseMessagePageDto);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_abort_session")]
    [Description("Aborts a running session. Preview only unless confirm=true. Do not target the calling session itself.")]
    public async Task<CallToolResult> AbortSessionAsync(
        [Description("Session id to abort.")] string sessionId,
        [Description("Must be true to execute. Without it only a preview is returned.")] bool confirm = false,
        [Description("Instance id. Omit to locate the session automatically.")] string? instanceId = null,
        [Description("Also abort busy child sessions.")] bool includeChildren = true,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return Preview(
                CommandTypes.SessionAbort,
                sessionId,
                instanceId,
                $"includeChildren={includeChildren}");
        }

        try
        {
            var response = await _client.AbortSessionAsync(sessionId, instanceId, includeChildren, ct).ConfigureAwait(false);
            return CommandResult(response);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_send_prompt")]
    [Description("Sends a prompt to an idle session without aborting it. Preview only unless confirm=true. Warning: targeting the calling session interrupts its turn.")]
    public async Task<CallToolResult> SendPromptAsync(
        [Description("Session id.")] string sessionId,
        [Description("Prompt text.")] string text,
        [Description("Must be true to execute. Without it only a preview is returned.")] bool confirm = false,
        [Description("Instance id. Omit to locate the session automatically.")] string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return Preview(CommandTypes.SessionPrompt, sessionId, instanceId, $"text={Truncate(text, MessageSummaryChars)}");
        }

        try
        {
            var response = await _client.SendPromptAsync(sessionId, instanceId, text, null, null, ct).ConfigureAwait(false);
            return CommandResult(response);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_abort_and_prompt")]
    [Description("Aborts the session if busy, waits for it to settle, then sends a prompt once. Preview only unless confirm=true. Warning: targeting the calling session interrupts its turn.")]
    public async Task<CallToolResult> AbortAndPromptAsync(
        [Description("Session id.")] string sessionId,
        [Description("Prompt text.")] string text,
        [Description("Must be true to execute. Without it only a preview is returned.")] bool confirm = false,
        [Description("Instance id. Omit to locate the session automatically.")] string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return Preview(CommandTypes.SessionAbortAndPrompt, sessionId, instanceId, $"text={Truncate(text, MessageSummaryChars)}");
        }

        try
        {
            var response = await _client.AbortAndPromptAsync(sessionId, instanceId, text, null, ct).ConfigureAwait(false);
            return CommandResult(response);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_list_workspaces", ReadOnly = true, Idempotent = true)]
    [Description("Lists the workspace roots registered on this machine.")]
    public async Task<CallToolResult> ListWorkspacesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.ListWorkspacesAsync(ct).ConfigureAwait(false);
            var lines = response.Data.Select(workspace =>
                $"- {workspace.WorkspaceId} {workspace.Name} ({workspace.Root}) upload={workspace.AllowUpload} download={workspace.AllowDownload} mkdir={workspace.AllowMkdir} delete={workspace.AllowDelete}");
            var summary = $"{Freshness(response.Meta)}\n{response.Data.Count} workspace(s):\n" + string.Join("\n", lines);

            return Result(summary, response, AcJsonContext.Default.ApiResponseListWorkspaceInfo);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_list_files", ReadOnly = true, Idempotent = true)]
    [Description("Lists one directory level inside a workspace. Denied entries are marked but never read.")]
    public async Task<CallToolResult> ListFilesAsync(
        [Description("Workspace id from local_list_workspaces.")] string workspaceId,
        [Description("Relative directory path. Omit or use an empty string for the workspace root.")] string? path = null,
        [Description("Sort: 'name' (default), 'modified', or 'size'.")] string sort = FileSorts.Name,
        [Description("Pagination cursor returned by a previous call.")] string? cursor = null,
        [Description("Maximum entries, 1-500. Defaults to 200.")] int limit = 200,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.ListFilesAsync(workspaceId, path, sort, cursor, limit, ct).ConfigureAwait(false);
            var lines = response.Data.Entries.Select(entry =>
                $"- {(entry.IsDir ? "dir " : "file")} {entry.RelPath} size={entry.Size?.ToString(CultureInfo.InvariantCulture) ?? "-"} modified={entry.Modified:O}{(entry.Denied ? " denied" : string.Empty)}");
            var summary = $"{Freshness(response.Meta)}\n{response.Data.Entries.Count} entrie(s) in {workspaceId}:{path ?? "/"}"
                + (response.Data.HasMore ? $"\nmore available, cursor={response.Data.NextCursor}" : string.Empty)
                + "\n"
                + string.Join("\n", lines);

            return Result(summary, response, AcJsonContext.Default.ApiResponseFileListPage);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "local_read_text_file", ReadOnly = true, Idempotent = true)]
    [Description("Reads a text preview of a workspace file. Binary and image files are rejected.")]
    public async Task<CallToolResult> ReadTextFileAsync(
        [Description("Workspace id from local_list_workspaces.")] string workspaceId,
        [Description("Relative file path.")] string path,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.PreviewFileAsync(workspaceId, path, ct).ConfigureAwait(false);
            var data = response.Data;
            if (data.Kind != "text" || data.Text is null)
            {
                return Text(
                    $"[{response.Meta.Source}] {data.RelPath} is {data.Kind} ({data.Size} bytes); use the Files screen to download it.",
                    isError: true);
            }

            var summary = $"{Freshness(response.Meta)}\n{data.RelPath} ({data.Size} bytes{(data.Truncated ? ", truncated" : string.Empty)}):\n{data.Text}";
            return Result(summary, response, AcJsonContext.Default.ApiResponseFilePreviewView);
        }
        catch (Exception ex) when (ex is LocalApiException or HttpRequestException)
        {
            return Error(ex);
        }
    }

    private static CallToolResult CommandResult(ApiResponse<CommandView> response)
    {
        var data = response.Data;
        var summary = $"{Freshness(response.Meta)}\nCommand {data.CommandId} ({data.Type}): {data.Status}"
            + (data.ErrorCode is null ? string.Empty : $" [{data.ErrorCode}] {data.ErrorMessage}");

        return Result(summary, response, AcJsonContext.Default.ApiResponseCommandView, isError: data.Status != CommandStatus.Succeeded);
    }

    private static CallToolResult Preview(string action, string sessionId, string? instanceId, string detail) =>
        Text(
            $"Preview only: no command was sent.\nAction: {action}\nSession: {sessionId}\nInstance: {instanceId ?? "(auto)"}\n{detail}\nCall again with confirm=true to execute.");

    private static CallToolResult Result<T>(
        string summary,
        T value,
        JsonTypeInfo<T> info,
        bool isError = false) =>
        new()
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = summary } },
            StructuredContent = JsonSerializer.SerializeToElement(value, info),
            IsError = isError,
        };

    private static CallToolResult Text(string summary, bool isError = false) =>
        new()
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = summary } },
            IsError = isError,
        };

    private static CallToolResult Error(Exception ex)
    {
        var code = ex is LocalApiException api ? api.Code : "network_error";
        return Text($"error [{code}]: {ex.Message}", isError: true);
    }

    private static string Freshness(ResponseMeta meta) =>
        string.Create(CultureInfo.InvariantCulture, $"[{meta.Source} {meta.LatencyMs / 1000.0:0.0}s]");

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "...";
}
