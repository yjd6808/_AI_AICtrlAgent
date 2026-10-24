using AiCtrl.Client.Commands;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Vpn;

public sealed record VpnRecoveryActionResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? Server = null,
    string? Message = null)
{
    public static VpnRecoveryActionResult Success(
        string? server = null,
        string? message = null) => new(true, Server: server, Message: message);

    public static VpnRecoveryActionResult Failure(
        string code,
        string? message = null) => new(false, code, Message: message);
}

public sealed record VpnRecoveryContext(
    VpnRecoveryNoticeKind Kind,
    ErrorClass ErrorClass = ErrorClass.Unknown,
    string? Message = null,
    RetryInfo? Retry = null,
    TimeSpan? StalledFor = null);

public interface IVpnRecoveryActions
{
    Task<VpnRecoveryActionResult> AbortSessionAsync(SessionRef session, CancellationToken ct);

    Task<VpnRecoveryActionResult> ConnectVpnAsync(VpnRecoveryContext context, CancellationToken ct);

    Task<VpnRecoveryActionResult> ResumeSessionAsync(
        SessionRef session,
        string prompt,
        CancellationToken ct);
}

public sealed class VpnRecoveryActions : IVpnRecoveryActions
{
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromHours(24);

    private readonly VpnSystem _vpn;

    private readonly IProviderInstanceRegistry _registry;

    private readonly AbortAndPromptRunner _runner;

    private readonly VpnRecoveryOptions _options;

    private readonly VpnPolicyStore _policy;

    private readonly TimeProvider _time;

    private readonly ILogger<VpnRecoveryActions> _logger;

    public VpnRecoveryActions(
        VpnSystem vpn,
        IProviderInstanceRegistry registry,
        AbortAndPromptRunner runner,
        VpnRecoveryOptions options,
        VpnPolicyStore policy,
        TimeProvider time,
        ILogger<VpnRecoveryActions> logger)
    {
        ArgumentNullException.ThrowIfNull(vpn);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _vpn = vpn;
        _registry = registry;
        _runner = runner;
        _options = options;
        _policy = policy;
        _time = time;
        _logger = logger;
    }

    public async Task<VpnRecoveryActionResult> AbortSessionAsync(
        SessionRef session,
        CancellationToken ct)
    {
        var instance = ResolveInstance(session);
        if (instance is null)
        {
            return VpnRecoveryActionResult.Failure(AcErrorCodes.InstanceNotFound);
        }

        var result = await _runner
            .AbortAsync(instance, session, CommandId("abort"), ct)
            .ConfigureAwait(false);
        return result.Succeeded
            ? VpnRecoveryActionResult.Success()
            : VpnRecoveryActionResult.Failure(
                result.ErrorCode ?? AcErrorCodes.InternalError,
                result.ErrorMessage);
    }

    public async Task<VpnRecoveryActionResult> ConnectVpnAsync(
        VpnRecoveryContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var policy = await _policy.GetAsync(ct).ConfigureAwait(false);
        var settings = await _policy.GetRecoveryAsync(ct).ConfigureAwait(false);
        if (!policy.Enabled)
        {
            return VpnRecoveryActionResult.Failure(
                "vpn_policy_disabled",
                "VPN policy is disabled.");
        }

        var status = await _vpn.GetStatusAsync(ct).ConfigureAwait(false);
        var currentServer = status.TunnelUp ? status.Server : null;
        var now = _time.GetUtcNow();
        var assessment = VpnFailureParser.Assess(
            context.ErrorClass,
            context.Message,
            context.Retry,
            context.Kind,
            now);
        var threshold = TimeSpan.FromSeconds(policy.RetryWaitThresholdSeconds);
        if (currentServer is not null
            && assessment.RetryAfter is { } shortDelay
            && shortDelay > TimeSpan.Zero
            && shortDelay < threshold)
        {
            await Task.Delay(shortDelay, _time, ct).ConfigureAwait(false);
            return VpnRecoveryActionResult.Success(
                currentServer,
                $"Waited {Math.Ceiling(shortDelay.TotalSeconds)} seconds before retrying the same VPN server.");
        }

        if (currentServer is not null)
        {
            var cooldown = assessment.RetryAfter ?? DefaultCooldown;
            await MarkFailureAsync(
                currentServer,
                assessment.ServerStatus,
                now + cooldown,
                assessment.Message,
                status.Country,
                status.City,
                ct).ConfigureAwait(false);
        }

        var servers = await _vpn.GetServerCatalogAsync(refresh: false, ct).ConfigureAwait(false);
        if (settings.Servers.Count > 0)
        {
            var allowed = new HashSet<string>(settings.Servers, StringComparer.OrdinalIgnoreCase);
            servers = servers
                .Where(server => allowed.Contains(server.Name))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(settings.Country))
        {
            servers = servers
                .Where(server => string.Equals(
                    server.Country,
                    settings.Country,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(settings.City))
        {
            servers = servers
                .Where(server => string.Equals(
                    server.City,
                    settings.City,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (servers.Count == 0)
        {
            return VpnRecoveryActionResult.Failure(
                "vpn_no_candidates",
                "No VPN servers were returned by the ProtonVPN CLI.");
        }

        var avoid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (currentServer is not null)
        {
            avoid.Add(currentServer);
        }

        var attempts = Math.Clamp(policy.MaxAttemptsPerSession, 1, 5);
        string? lastError = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            policy = await _policy.GetAsync(ct).ConfigureAwait(false);
            var selection = VpnServerSelector.Select(
                policy,
                servers,
                currentServer,
                avoid,
                _time.GetUtcNow());
            if (selection.Selected is not { } candidate)
            {
                var next = selection.NextAvailableAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown";
                return VpnRecoveryActionResult.Failure(
                    "vpn_no_candidates",
                    $"No eligible VPN server remains. Earliest availability: {next}.");
            }

            try
            {
                var result = await _vpn.ConnectAsync(
                    new VpnConnectPayload(
                        candidate.Name,
                        candidate.Country,
                        candidate.City,
                        Force: true),
                    CommandId("switch"),
                    ct).ConfigureAwait(false);
                var connectedServer = result.Connect?.Server ?? result.Status.Server;
                var exitIp = result.Connect?.ExitIp ?? result.Status.ExitIp;
                if (!result.Status.TunnelUp || string.IsNullOrWhiteSpace(connectedServer))
                {
                    lastError = "The VPN tunnel did not become ready.";
                    await MarkFailureAsync(
                        candidate.Name,
                        VpnServerStatuses.ConnectFailed,
                        _time.GetUtcNow() + DefaultCooldown,
                        lastError,
                        candidate.Country,
                        candidate.City,
                        ct).ConfigureAwait(false);
                    avoid.Add(candidate.Name);
                    continue;
                }

                if (currentServer is not null
                    && !string.IsNullOrWhiteSpace(status.ExitIp)
                    && !string.IsNullOrWhiteSpace(exitIp)
                    && string.Equals(status.ExitIp, exitIp, StringComparison.Ordinal)
                    && !string.Equals(currentServer, connectedServer, StringComparison.OrdinalIgnoreCase))
                {
                    lastError = "The exit IP did not change after switching servers.";
                    await MarkFailureAsync(
                        candidate.Name,
                        VpnServerStatuses.ConnectFailed,
                        _time.GetUtcNow() + DefaultCooldown,
                        lastError,
                        candidate.Country,
                        candidate.City,
                        ct).ConfigureAwait(false);
                    avoid.Add(candidate.Name);
                    continue;
                }

                return VpnRecoveryActionResult.Success(connectedServer);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (VpnException ex)
            {
                lastError = ex.Message;
                await MarkFailureAsync(
                    candidate.Name,
                    VpnServerStatuses.ConnectFailed,
                    _time.GetUtcNow() + DefaultCooldown,
                    ex.Message,
                    candidate.Country,
                    candidate.City,
                    ct).ConfigureAwait(false);
                avoid.Add(candidate.Name);
            }
        }

        return VpnRecoveryActionResult.Failure(
            "vpn_failover_exhausted",
            lastError ?? "Every eligible VPN server failed to connect.");
    }

    public async Task<VpnRecoveryActionResult> ResumeSessionAsync(
        SessionRef session,
        string prompt,
        CancellationToken ct)
    {
        var instance = ResolveInstance(session);
        if (instance is null)
        {
            return VpnRecoveryActionResult.Failure(AcErrorCodes.InstanceNotFound);
        }

        var summary = await instance.GetSessionAsync(session.SessionId, ct).ConfigureAwait(false);
        if (summary is null)
        {
            return VpnRecoveryActionResult.Failure(AcErrorCodes.SessionNotFound);
        }

        var effectivePrompt = string.IsNullOrWhiteSpace(prompt)
            ? _options.ToSettings().ContinuationPrompt
            : prompt;
        var payload = new SessionAbortAndPromptPayload(session, effectivePrompt, summary.LastModel);
        var result = await _runner
            .PromptAsync(instance, payload, CommandId("resume"), ct)
            .ConfigureAwait(false);
        return result.Succeeded
            ? VpnRecoveryActionResult.Success()
            : VpnRecoveryActionResult.Failure(
                result.ErrorCode ?? AcErrorCodes.InternalError,
                result.ErrorMessage);
    }

    private IProviderInstance? ResolveInstance(SessionRef session) =>
        _registry.Instances.FirstOrDefault(candidate => candidate.Info.Id == session.Instance);

    private async Task MarkFailureAsync(
        string server,
        string status,
        DateTimeOffset? availableAt,
        string? error,
        string? country,
        string? city,
        CancellationToken ct)
    {
        try
        {
            await _policy.MarkFailureAsync(
                server,
                status,
                availableAt,
                error,
                country,
                city,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not record VPN failure for {Server}.", server);
        }
    }

    private static string CommandId(string prefix) =>
        "vpn_recovery_" + prefix + "_" + Guid.NewGuid().ToString("N");
}
