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

    public void Dispose()
    {
        if (_owns)
        {
            _http.Dispose();
        }
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
