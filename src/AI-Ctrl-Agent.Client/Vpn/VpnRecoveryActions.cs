using AiCtrl.Client.Commands;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Vpn;

public sealed record VpnRecoveryActionResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? Server = null)
{
    public static VpnRecoveryActionResult Success(string? server = null) => new(true, Server: server);

    public static VpnRecoveryActionResult Failure(string code) => new(false, code);
}

public interface IVpnRecoveryActions
{
    Task<VpnRecoveryActionResult> ConnectVpnAsync(CancellationToken ct);

    Task<VpnRecoveryActionResult> ResumeSessionAsync(SessionRef session, CancellationToken ct);
}

public sealed class VpnRecoveryActions : IVpnRecoveryActions
{
    private const string DefaultContinuationPrompt =
        "Continue from where you stopped. Do not repeat completed work.";

    private readonly VpnSystem _vpn;

    private readonly IProviderInstanceRegistry _registry;

    private readonly AbortAndPromptRunner _runner;

    private readonly VpnRecoveryOptions _options;

    private int _serverIndex = -1;

    public VpnRecoveryActions(
        VpnSystem vpn,
        IProviderInstanceRegistry registry,
        AbortAndPromptRunner runner,
        VpnRecoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(vpn);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        _vpn = vpn;
        _registry = registry;
        _runner = runner;
        _options = options;
    }

    public async Task<VpnRecoveryActionResult> ConnectVpnAsync(CancellationToken ct)
    {
        var payload = new VpnConnectPayload(
            NextServer(),
            _options.Country,
            _options.City,
            Force: true);
        try
        {
            var result = await _vpn
                .ConnectAsync(payload, CommandId(), ct)
                .ConfigureAwait(false);
            return VpnRecoveryActionResult.Success(result.Connect?.Server ?? result.Status.Server);
        }
        catch (VpnException ex)
        {
            return VpnRecoveryActionResult.Failure(ex.Code);
        }
    }

    public async Task<VpnRecoveryActionResult> ResumeSessionAsync(
        SessionRef session,
        CancellationToken ct)
    {
        var instance = _registry.Instances.FirstOrDefault(
            candidate => candidate.Info.Id == session.Instance);
        if (instance is null)
        {
            return VpnRecoveryActionResult.Failure(AcErrorCodes.InstanceNotFound);
        }

        var summary = await instance.GetSessionAsync(session.SessionId, ct).ConfigureAwait(false);
        if (summary is null)
        {
            return VpnRecoveryActionResult.Failure(AcErrorCodes.SessionNotFound);
        }

        var prompt = string.IsNullOrWhiteSpace(_options.ContinuationPrompt)
            ? DefaultContinuationPrompt
            : _options.ContinuationPrompt;
        var payload = new SessionAbortAndPromptPayload(session, prompt, summary.LastModel);
        var result = await _runner
            .RunAsync(instance, payload, CommandId(), ct)
            .ConfigureAwait(false);
        return result.Succeeded
            ? VpnRecoveryActionResult.Success()
            : VpnRecoveryActionResult.Failure(result.ErrorCode ?? AcErrorCodes.InternalError);
    }

    private string? NextServer()
    {
        if (_options.Servers.Count == 0)
        {
            return null;
        }

        var index = Interlocked.Increment(ref _serverIndex);
        return _options.Servers[(int)((uint)index % (uint)_options.Servers.Count)];
    }

    private static string CommandId() => "vpn_recovery_" + Guid.NewGuid().ToString("N");
}
