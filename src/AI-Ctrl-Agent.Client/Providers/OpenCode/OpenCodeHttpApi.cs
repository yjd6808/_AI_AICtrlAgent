using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed class OpenCodeHttpApi
{
    private static readonly JsonSerializerOptions OutgoingJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _client;
    private readonly AuthenticationHeaderValue _authorization;

    public OpenCodeHttpApi(HttpClient client, string username, string password)
    {
        _client = client;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        _authorization = new AuthenticationHeaderValue("Basic", raw);
    }

    public async Task<(HttpStatusCode Status, JsonElement? Body, string? Raw)> TryGetJsonAsync(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, ParseTolerant(raw), raw);
    }

    public async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        var (status, body, raw) = await TryGetJsonAsync(path, ct).ConfigureAwait(false);
        if (status is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            throw new OpenCodeHttpException(status, raw);
        }

        return body;
    }

    public async Task<JsonElement?> PostJsonAsync(string path, IReadOnlyDictionary<string, object?> body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        var json = JsonSerializer.Serialize(body, OutgoingJson);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            throw new OpenCodeHttpException(response.StatusCode, raw);
        }

        return ParseTolerant(raw);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = _authorization;
        return request;
    }

    private static JsonElement? ParseTolerant(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
