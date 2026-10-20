using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Api;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;

namespace AiCtrl.Server.Security;

public sealed record EnrollmentServiceResult(bool Success, string? ErrorCode, string? MachineId);

public sealed class EnrollmentService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly ServerDb _db;

    private readonly TimeProvider _time;

    public EnrollmentService(ServerDb db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _time = time;
    }

    public async Task<EnrollmentCodeResponse> CreateCodeAsync(CancellationToken ct)
    {
        var code = EnrollmentCodeFormat.Generate();
        var normalized = EnrollmentCodeFormat.Normalize(code);
        var expiresAt = _time.GetUtcNow().Add(CodeLifetime);
        await _db.CreateEnrollmentCodeAsync(Hash(normalized), expiresAt, ct).ConfigureAwait(false);
        return new EnrollmentCodeResponse(code, expiresAt);
    }

    public async Task<EnrollmentServiceResult> EnrollAsync(EnrollmentRequest request, string? ip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Hostname)
            || string.IsNullOrWhiteSpace(request.Os)
            || string.IsNullOrWhiteSpace(request.ClientVersion)
            || request.Hostname.Length > 255
            || request.Os.Length > 100
            || request.ClientVersion.Length > 100)
        {
            return new EnrollmentServiceResult(false, AcErrorCodes.InvalidPayload, null);
        }

        if (!TryValidatePublicKey(request.PublicKey))
        {
            return new EnrollmentServiceResult(false, AcErrorCodes.InvalidPayload, null);
        }

        var normalized = EnrollmentCodeFormat.Normalize(request.Code);
        if (normalized.Length != EnrollmentCodeFormat.Length)
        {
            return new EnrollmentServiceResult(false, AcErrorCodes.InvalidCode, null);
        }

        var machineId = "m_" + Guid.NewGuid().ToString("N");
        var result = await _db.EnrollAsync(
            Hash(normalized),
            machineId,
            request.Hostname,
            request.Os,
            request.PublicKey,
            request.ClientVersion,
            "enrollment-code",
            ip,
            ct).ConfigureAwait(false);

        return result.Success
            ? new EnrollmentServiceResult(true, null, machineId)
            : new EnrollmentServiceResult(false, result.ErrorCode, null);
    }

    public static string Hash(string normalizedCode)
    {
        ArgumentNullException.ThrowIfNull(normalizedCode);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));
    }

    private static bool TryValidatePublicKey(string publicKey)
    {
        try
        {
            var bytes = Convert.FromBase64String(publicKey);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && ecdsa.KeySize == 256;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
