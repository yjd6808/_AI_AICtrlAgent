namespace AiCtrl.Client.Providers.OpenCode.Backend;

public enum BackendHealthStatus
{
    Healthy,
    Unauthorized,
    Foreign,
    Unreachable,
}

public sealed record BackendHealth(BackendHealthStatus Status, string? Version);

public interface IBackendHealthProbe
{
    ValueTask<BackendHealth> ProbeAsync(CancellationToken ct);
}
