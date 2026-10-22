using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;

namespace AiCtrl.Client.Link;

public sealed class AcApiException : Exception
{
    public AcApiException(int status, string code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }

    public string Code { get; }
}

public sealed class ServerApiClient : IDisposable
{
    private readonly HttpClient _http;

    private readonly bool _owns;

    public ServerApiClient(string baseUrl, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        BaseUrl = baseUrl.TrimEnd('/');
        _owns = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string BaseUrl { get; }

    public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken ct)
    {
        using var response = await PostAsync("/api/v1/enroll", request, AcJsonContext.Default.EnrollmentRequest, ct).ConfigureAwait(false);
        return await ReadAsync(response, AcJsonContext.Default.EnrollmentResponse, ct).ConfigureAwait(false);
    }

    public async Task<AgentChallengeResponse> ChallengeAsync(MachineId machineId, CancellationToken ct)
    {
        var request = new AgentChallengeRequest(machineId);
        using var response = await PostAsync("/api/v1/agent/challenge", request, AcJsonContext.Default.AgentChallengeRequest, ct).ConfigureAwait(false);
        return await ReadAsync(response, AcJsonContext.Default.AgentChallengeResponse, ct).ConfigureAwait(false);
    }

    public async Task<AgentTokenResponse> TokenAsync(AgentTokenRequest request, CancellationToken ct)
    {
        using var response = await PostAsync("/api/v1/agent/token", request, AcJsonContext.Default.AgentTokenRequest, ct).ConfigureAwait(false);
        return await ReadAsync(response, AcJsonContext.Default.AgentTokenResponse, ct).ConfigureAwait(false);
    }

    public async Task<TransferStatusView> GetTransferAsync(
        string transferId,
        string token,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/api/v1/agent/transfers/" + Uri.EscapeDataString(transferId),
            token,
            null,
            ct).ConfigureAwait(false);
        var body = await ReadAsync(response, AcJsonContext.Default.ApiResponseTransferStatusView, ct).ConfigureAwait(false);
        return body.Data;
    }

    public async Task<TransferStatusView> BeginTransferAsync(
        string transferId,
        TransferBeginRequest request,
        string token,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/agent/transfers/" + Uri.EscapeDataString(transferId) + "/begin",
            token,
            JsonContent.Create(request, AcJsonContext.Default.TransferBeginRequest),
            ct).ConfigureAwait(false);
        var body = await ReadAsync(response, AcJsonContext.Default.ApiResponseTransferStatusView, ct).ConfigureAwait(false);
        return body.Data;
    }

    public async Task<TransferStatusView> PutTransferChunkAsync(
        string transferId,
        int index,
        ReadOnlyMemory<byte> data,
        string token,
        CancellationToken ct)
    {
        using var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(
            HttpMethod.Put,
            "/api/v1/agent/transfers/" + Uri.EscapeDataString(transferId) + "/chunks/"
                + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            token,
            content,
            ct).ConfigureAwait(false);
        var body = await ReadAsync(response, AcJsonContext.Default.ApiResponseTransferStatusView, ct).ConfigureAwait(false);
        return body.Data;
    }

    public async Task<TransferStatusView> CompleteTransferAsync(
        string transferId,
        string token,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/agent/transfers/" + Uri.EscapeDataString(transferId) + "/complete",
            token,
            null,
            ct).ConfigureAwait(false);
        var body = await ReadAsync(response, AcJsonContext.Default.ApiResponseTransferStatusView, ct).ConfigureAwait(false);
        return body.Data;
    }

    public async Task CopyTransferContentAsync(
        string transferId,
        string token,
        long offset,
        Stream destination,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(BaseUrl + "/api/v1/agent/transfers/" + Uri.EscapeDataString(transferId) + "/content"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, ct).ConfigureAwait(false);
            throw new AcApiException(
                (int)response.StatusCode,
                problem?.Code ?? AcErrorCodes.InternalError,
                problem?.Detail ?? response.ReasonPhrase ?? "Transfer content request failed.");
        }

        await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public async Task ReleaseTransferAsync(string transferId, string token, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/agent/transfers/" + Uri.EscapeDataString(transferId) + "/release",
            token,
            null,
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, ct).ConfigureAwait(false);
            throw new AcApiException(
                (int)response.StatusCode,
                problem?.Code ?? AcErrorCodes.InternalError,
                problem?.Detail ?? response.ReasonPhrase ?? "Transfer release failed.");
        }
    }

    public void Dispose()
    {
        if (_owns)
        {
            _http.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string token,
        HttpContent? content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseUrl + path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        using var content = JsonContent.Create(value, typeInfo, mediaType: new MediaTypeHeaderValue("application/json"));
        return await _http.PostAsync(new Uri(BaseUrl + path), content, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, ct).ConfigureAwait(false)
                ?? throw new AcApiException((int)response.StatusCode, AcErrorCodes.InvalidPayload, "Response body is empty.");
        }

        var problem = await ReadProblemAsync(response, ct).ConfigureAwait(false);
        throw new AcApiException((int)response.StatusCode, problem?.Code ?? AcErrorCodes.InternalError, problem?.Detail ?? response.ReasonPhrase ?? "Request failed.");
    }

    private static async Task<AcProblemDetails?> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(AcJsonContext.Default.AcProblemDetails, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
