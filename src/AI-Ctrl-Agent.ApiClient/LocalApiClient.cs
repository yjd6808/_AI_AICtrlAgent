using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Config;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;

namespace AiCtrl.ApiClient;

public sealed class LocalApiException : Exception
{
    public LocalApiException(HttpStatusCode statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }
}

public sealed class LocalApiClient : IDisposable
{
    public const int DefaultPort = 47800;

    private const string LocalPrefix = "/local/v1";

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly string _tokenPath;

    private string? _token;

    public LocalApiClient(Uri baseAddress, string tokenPath, TimeSpan? timeout = null)
        : this(new HttpClient(), baseAddress, tokenPath, timeout, ownsHttp: true)
    {
    }

    public LocalApiClient(HttpClient http, Uri baseAddress, string tokenPath, TimeSpan? timeout = null, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        _http = http;
        _ownsHttp = ownsHttp;
        _tokenPath = tokenPath;
        _http.BaseAddress = baseAddress;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(90);
    }

    public static LocalApiClient CreateFromEnvironment()
    {
        var portValue = Environment.GetEnvironmentVariable("AICTRL_LOCAL_PORT");
        var port = int.TryParse(portValue, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : DefaultPort;
        var tokenPath = Environment.GetEnvironmentVariable("AICTRL_LOCAL_TOKEN");
        if (string.IsNullOrWhiteSpace(tokenPath))
        {
            tokenPath = Path.Combine(
                AcDefaultConfigs.AgentHome,
                "data",
                "local-token");
        }

        return new LocalApiClient(new Uri($"http://127.0.0.1:{port}/"), tokenPath);
    }

    public Task<ApiResponse<LocalStatusDto>> GetStatusAsync(CancellationToken ct) =>
        GetAsync($"{LocalPrefix}/status", AcJsonContext.Default.ApiResponseLocalStatusDto, ct);

    public Task<ApiResponse<List<InstanceInfo>>> ListInstancesAsync(CancellationToken ct) =>
        GetAsync($"{LocalPrefix}/instances", AcJsonContext.Default.ApiResponseListInstanceInfo, ct);

    public Task<ApiResponse<SessionPageDto>> ListSessionsAsync(
        string? instanceId,
        string view,
        CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(instanceId)
            ? $"{LocalPrefix}/sessions{Query(("view", view))}"
            : $"{LocalPrefix}/instances/{Escape(instanceId)}/sessions{Query(("view", view))}";

        return GetAsync(path, AcJsonContext.Default.ApiResponseSessionPageDto, ct);
    }

    public Task<ApiResponse<MessagePageDto>> GetMessagesAsync(
        string sessionId,
        string? instanceId,
        int limit,
        string? cursor,
        CancellationToken ct)
    {
        var path = $"{LocalPrefix}/sessions/{Escape(sessionId)}/messages"
            + Query(("instance", instanceId), ("limit", limit.ToString()), ("cursor", cursor));

        return GetAsync(path, AcJsonContext.Default.ApiResponseMessagePageDto, ct);
    }

    public Task<ApiResponse<CommandView>> AbortSessionAsync(
        string sessionId,
        string? instanceId,
        bool includeChildren,
        CancellationToken ct)
    {
        var path = $"{LocalPrefix}/sessions/{Escape(sessionId)}/abort{Query(("instance", instanceId))}";
        var body = JsonSerializer.SerializeToElement(
            new AbortSessionBody(includeChildren),
            AcJsonContext.Default.AbortSessionBody);

        return PostAsync(path, body, AcJsonContext.Default.ApiResponseCommandView, ct);
    }

    public Task<ApiResponse<CommandView>> SendPromptAsync(
        string sessionId,
        string? instanceId,
        string text,
        ModelRef? model,
        string? agent,
        CancellationToken ct)
    {
        var path = $"{LocalPrefix}/sessions/{Escape(sessionId)}/prompt{Query(("instance", instanceId))}";
        var body = JsonSerializer.SerializeToElement(
            new PromptBody(text, model, agent),
            AcJsonContext.Default.PromptBody);

        return PostAsync(path, body, AcJsonContext.Default.ApiResponseCommandView, ct);
    }

    public Task<ApiResponse<CommandView>> AbortAndPromptAsync(
        string sessionId,
        string? instanceId,
        string text,
        ModelRef? model,
        CancellationToken ct)
    {
        var path = $"{LocalPrefix}/sessions/{Escape(sessionId)}/abort-and-prompt{Query(("instance", instanceId))}";
        var body = JsonSerializer.SerializeToElement(
            new AbortAndPromptBody(text, model),
            AcJsonContext.Default.AbortAndPromptBody);

        return PostAsync(path, body, AcJsonContext.Default.ApiResponseCommandView, ct);
    }

    public Task<ApiResponse<CommandView>> GetCommandAsync(string commandId, CancellationToken ct) =>
        GetAsync($"{LocalPrefix}/commands/{Escape(commandId)}", AcJsonContext.Default.ApiResponseCommandView, ct);

    public Task<ApiResponse<List<WorkspaceInfo>>> ListWorkspacesAsync(CancellationToken ct) =>
        GetAsync($"{LocalPrefix}/workspaces", AcJsonContext.Default.ApiResponseListWorkspaceInfo, ct);

    public Task<ApiResponse<FileListPage>> ListFilesAsync(
        string workspaceId,
        string? path,
        string sort,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        var query = Query(
            ("workspaceId", workspaceId),
            ("path", path),
            ("sort", sort),
            ("cursor", cursor),
            ("limit", limit.ToString()));
        return GetAsync($"{LocalPrefix}/files{query}", AcJsonContext.Default.ApiResponseFileListPage, ct);
    }

    public Task<ApiResponse<FileStatView>> StatFileAsync(
        string workspaceId,
        string path,
        bool hash,
        CancellationToken ct)
    {
        var query = Query(
            ("workspaceId", workspaceId),
            ("path", path),
            ("hash", hash ? "true" : "false"));
        return GetAsync($"{LocalPrefix}/files/stat{query}", AcJsonContext.Default.ApiResponseFileStatView, ct);
    }

    public Task<ApiResponse<FilePreviewView>> PreviewFileAsync(
        string workspaceId,
        string path,
        CancellationToken ct)
    {
        var query = Query(("workspaceId", workspaceId), ("path", path));
        return GetAsync($"{LocalPrefix}/files/preview{query}", AcJsonContext.Default.ApiResponseFilePreviewView, ct);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<ApiResponse<T>> GetAsync<T>(string path, JsonTypeInfo<ApiResponse<T>> info, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(request, info, ct).ConfigureAwait(false);
    }

    private async Task<ApiResponse<T>> PostAsync<T>(
        string path,
        JsonElement body,
        JsonTypeInfo<ApiResponse<T>> info,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.GetRawText(), System.Text.Encoding.UTF8, "application/json"),
        };

        return await SendAsync(request, info, ct).ConfigureAwait(false);
    }

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpRequestMessage request,
        JsonTypeInfo<ApiResponse<T>> info,
        CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var problem = TryReadProblem(json);
            throw new LocalApiException(
                response.StatusCode,
                problem?.Code ?? $"http_{(int)response.StatusCode}",
                problem?.Detail ?? problem?.Title ?? $"Local API request failed with status {(int)response.StatusCode}.");
        }

        return JsonSerializer.Deserialize(json, info)
            ?? throw new LocalApiException(
                response.StatusCode,
                AcErrorCodes.InternalError,
                "The local API returned an empty response.");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null)
        {
            return _token;
        }

        if (!File.Exists(_tokenPath))
        {
            throw new LocalApiException(
                HttpStatusCode.Unauthorized,
                AcErrorCodes.Unauthorized,
                $"The local API token file '{_tokenPath}' does not exist. Start the client first.");
        }

        _token = (await File.ReadAllTextAsync(_tokenPath, ct).ConfigureAwait(false)).Trim();
        if (_token.Length == 0)
        {
            throw new LocalApiException(
                HttpStatusCode.Unauthorized,
                AcErrorCodes.Unauthorized,
                $"The local API token file '{_tokenPath}' is empty.");
        }

        return _token;
    }

    private static AcProblemDetails? TryReadProblem(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, AcJsonContext.Default.AcProblemDetails);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Escape(pair.Key)}={Escape(pair.Value!)}")
            .ToList();

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
