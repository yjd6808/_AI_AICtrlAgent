using System.Text.Json;

namespace AiCtrl.Client.Vpn;

public enum VpnFallbackMode
{
    TunnelReady,
    ServerLink,
}

public sealed record VpnFallbackOptions
{
    public static VpnFallbackOptions Default { get; } = new();

    public VpnFallbackMode Mode { get; init; } = VpnFallbackMode.TunnelReady;

    public Uri ReadyUri { get; init; } = new("http://127.0.0.1:20241/ready");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public interface IVpnReadinessProbe
{
    Task<bool> IsReadyAsync(Uri uri, CancellationToken ct);
}

public sealed class HttpVpnReadinessProbe : IVpnReadinessProbe
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    public async Task<bool> IsReadyAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(uri, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

public sealed record VpnProgress(string Step, JsonElement? Data);
