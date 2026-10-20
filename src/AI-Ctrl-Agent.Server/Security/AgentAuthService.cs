using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;

namespace AiCtrl.Server.Security;

public sealed record AgentAuthResult(bool Success, string? ErrorCode, string? Value, DateTimeOffset ExpiresAt);

public sealed class AgentAuthService
{
    private readonly ServerDb _db;

    private readonly ChallengeStore _challenges;

    private readonly AgentTokenService _tokens;

    private readonly TimeProvider _time;

    public AgentAuthService(ServerDb db, ChallengeStore challenges, AgentTokenService tokens, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _challenges = challenges;
        _tokens = tokens;
        _time = time;
    }

    public async Task<AgentAuthResult> ChallengeAsync(MachineId machineId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId.Value, ct).ConfigureAwait(false);
        if (machine is null)
        {
            return new AgentAuthResult(false, AcErrorCodes.MachineNotFound, null, DateTimeOffset.MinValue);
        }

        if (machine.RevokedAt is not null)
        {
            return new AgentAuthResult(false, AcErrorCodes.MachineRevoked, null, DateTimeOffset.MinValue);
        }

        var nonce = _challenges.Create(machineId.Value);
        return new AgentAuthResult(true, null, nonce, _time.GetUtcNow().Add(AgentTokenDefaults.ChallengeLifetime));
    }

    public async Task<AgentAuthResult> IssueTokenAsync(AgentTokenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var machine = await _db.GetMachineAsync(request.MachineId.Value, ct).ConfigureAwait(false);
        if (machine is null)
        {
            return new AgentAuthResult(false, AcErrorCodes.MachineNotFound, null, DateTimeOffset.MinValue);
        }

        if (machine.RevokedAt is not null)
        {
            return new AgentAuthResult(false, AcErrorCodes.MachineRevoked, null, DateTimeOffset.MinValue);
        }

        if (!_challenges.TryConsume(request.MachineId.Value, request.Nonce))
        {
            return new AgentAuthResult(false, AcErrorCodes.ChallengeExpired, null, DateTimeOffset.MinValue);
        }

        if (!VerifySignature(machine.PublicKey, request.MachineId.Value, request.Nonce, request.Signature))
        {
            return new AgentAuthResult(false, AcErrorCodes.InvalidSignature, null, DateTimeOffset.MinValue);
        }

        var token = _tokens.Create(request.MachineId.Value, out var expiresAt);
        return new AgentAuthResult(true, null, token, expiresAt);
    }

    internal static bool VerifySignature(string publicKey, string machineId, string nonce, string signature)
    {
        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKey);
            var signatureBytes = Convert.FromBase64String(signature);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
            var payload = Encoding.UTF8.GetBytes($"aictrl-agent-v1|{machineId}|{nonce}");
            return ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
