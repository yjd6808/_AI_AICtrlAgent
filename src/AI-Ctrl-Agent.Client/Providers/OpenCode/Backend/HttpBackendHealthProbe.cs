using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiCtrl.Client.Providers.OpenCode.Backend;

public sealed class HttpBackendHealthProbe : IBackendHealthProbe
{
    private readonly HttpClient _client;
    private readonly string _healthPath;
    private readonly AuthenticationHeaderValue _authorization;

    public HttpBackendHealthProbe(HttpClient client, string healthPath, string username, string password)
    {
        _client = client;
        _healthPath = healthPath;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        _authorization = new AuthenticationHeaderValue("Basic", raw);
    }

    public async ValueTask<BackendHealth> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _healthPath);
            request.Headers.Authorization = _authorization;
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new BackendHealth(BackendHealthStatus.Unauthorized, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new BackendHealth(BackendHealthStatus.Foreign, null);
            }

            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new BackendHealth(BackendHealthStatus.Healthy, ReadVersion(raw));
        }
        catch (HttpRequestException)
        {
            return new BackendHealth(BackendHealthStatus.Unreachable, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BackendHealth(BackendHealthStatus.Unreachable, null);
        }
    }

    private static string? ReadVersion(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("version", out var version) &&
                   version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
