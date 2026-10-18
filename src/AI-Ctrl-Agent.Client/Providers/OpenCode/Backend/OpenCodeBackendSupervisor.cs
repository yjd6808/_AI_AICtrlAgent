using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Providers.OpenCode.Backend;

public enum BackendStatus
{
    Stopped,
    Starting,
    Healthy,
    Unhealthy,
    Failed,
}

public sealed record BackendState(
    BackendStatus Status,
    string? FailReason,
    string? Version,
    int? ProcessId,
    bool Adopted);

public sealed record OpenCodeBackendSupervisorOptions
{
    public required string Endpoint { get; init; }

    public string HealthPath { get; init; } = "/global/health";

    public string Username { get; init; } = "opencode";

    public string PasswordKey { get; init; } = "opencode.serve.password";

    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int WatchFailureThreshold { get; init; } = 3;

    public TimeSpan InitialRestartBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRestartBackoff { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class OpenCodeBackendSupervisor : IAsyncDisposable
{
    private readonly OpenCodeBackendSupervisorOptions _options;
    private readonly Func<string, IBackendProcessController> _controllerFactory;
    private readonly IBackendHealthProbe _probe;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private IBackendProcess? _process;
    private string? _password;
    private TimeSpan _restartBackoff;

    public OpenCodeBackendSupervisor(
        OpenCodeBackendSupervisorOptions options,
        Func<string, IBackendProcessController> controllerFactory,
        IBackendHealthProbe probe,
        ISecretStore secrets,
        TimeProvider time,
        ILogger logger)
    {
        _options = options;
        _controllerFactory = controllerFactory;
        _probe = probe;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _restartBackoff = options.InitialRestartBackoff;
        State = new BackendState(BackendStatus.Stopped, null, null, null, Adopted: false);
    }

    public BackendState State { get; private set; }

    public string? Password => _password;

    public async ValueTask<BackendState> StartAsync(CancellationToken ct)
    {
        _password = await EnsurePasswordAsync(ct).ConfigureAwait(false);

        var health = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        if (health.Status == BackendHealthStatus.Healthy)
        {
            State = new BackendState(BackendStatus.Healthy, null, health.Version, null, Adopted: true);
            _logger.LogInformation("Adopted running OpenCode backend at {Endpoint}.", _options.Endpoint);
            return State;
        }

        if (health.Status is BackendHealthStatus.Unauthorized or BackendHealthStatus.Foreign)
        {
            State = new BackendState(BackendStatus.Failed, "port_in_use", null, null, Adopted: false);
            _logger.LogWarning("Port {Endpoint} is occupied by a foreign responder ({Status}).", _options.Endpoint, health.Status);
            return State;
        }

        return await StartManagedAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask WatchAsync(CancellationToken ct)
    {
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.WatchInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            BackendHealth health;
            try
            {
                health = await _probe.ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (health.Status == BackendHealthStatus.Healthy)
            {
                failures = 0;
                _restartBackoff = _options.InitialRestartBackoff;
                if (State.Status != BackendStatus.Healthy)
                {
                    State = State with { Status = BackendStatus.Healthy, FailReason = null, Version = health.Version ?? State.Version };
                }

                continue;
            }

            failures++;
            _logger.LogWarning(
                "Backend health probe returned {Status} ({Failures}/{Threshold}).",
                health.Status,
                failures,
                _options.WatchFailureThreshold);
            if (failures < _options.WatchFailureThreshold)
            {
                continue;
            }

            failures = 0;

            if (State.Adopted)
            {
                State = State with { Status = BackendStatus.Unhealthy };
                continue;
            }

            var restarted = await RestartAsync(ct).ConfigureAwait(false);
            if (!restarted)
            {
                try
                {
                    await Task.Delay(_restartBackoff, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                failures = _options.WatchFailureThreshold - 1;
                _restartBackoff = NextBackoff(_restartBackoff);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!State.Adopted)
        {
            await KillProcessAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<BackendState> StartManagedAsync(CancellationToken ct)
    {
        State = new BackendState(BackendStatus.Starting, null, null, null, Adopted: false);

        try
        {
            _process = await _controllerFactory(_password!).StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to start managed OpenCode backend process.");
            State = new BackendState(BackendStatus.Failed, "start_failed", null, null, Adopted: false);
            return State;
        }

        var healthy = await PollUntilHealthyAsync(ct).ConfigureAwait(false);
        if (!healthy.Success)
        {
            await KillProcessAsync().ConfigureAwait(false);
            State = new BackendState(BackendStatus.Failed, "health_timeout", null, null, Adopted: false);
            return State;
        }

        State = new BackendState(BackendStatus.Healthy, null, healthy.Version, _process!.Id, Adopted: false);
        _logger.LogInformation("Started managed OpenCode backend at {Endpoint} (pid {Pid}).", _options.Endpoint, State.ProcessId);
        return State;
    }

    private async ValueTask<bool> RestartAsync(CancellationToken ct)
    {
        _logger.LogWarning("Restarting managed OpenCode backend process.");
        await KillProcessAsync().ConfigureAwait(false);

        try
        {
            _process = await _controllerFactory(_password!).StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to restart managed OpenCode backend process.");
            State = State with { Status = BackendStatus.Failed, FailReason = "restart_failed" };
            return false;
        }

        var healthy = await PollUntilHealthyAsync(ct).ConfigureAwait(false);
        if (!healthy.Success)
        {
            await KillProcessAsync().ConfigureAwait(false);
            State = State with { Status = BackendStatus.Failed, FailReason = "health_timeout" };
            return false;
        }

        State = new BackendState(BackendStatus.Healthy, null, healthy.Version, _process.Id, Adopted: false);
        return true;
    }

    private async ValueTask<(bool Success, string? Version)> PollUntilHealthyAsync(CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _options.StartTimeout;

        while (true)
        {
            var health = await _probe.ProbeAsync(ct).ConfigureAwait(false);
            if (health.Status == BackendHealthStatus.Healthy)
            {
                return (true, health.Version);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return (false, null);
            }

            await Task.Delay(_options.HealthPollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask KillProcessAsync()
    {
        if (_process is { } process)
        {
            await process.KillAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            _process = null;
        }
    }

    private async ValueTask<string> EnsurePasswordAsync(CancellationToken ct)
    {
        var existing = await _secrets.LoadAsync(_options.PasswordKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await _secrets.SaveAsync(_options.PasswordKey, generated, ct).ConfigureAwait(false);
        return generated;
    }

    private TimeSpan NextBackoff(TimeSpan current) =>
        TimeSpan.FromTicks(Math.Min(current.Ticks * 2, _options.MaxRestartBackoff.Ticks));
}
