using System.Security.Cryptography;
using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Protocol;

public sealed record EnrollmentRequest(
    string Code,
    string PublicKey,
    string Hostname,
    string Os,
    string ClientVersion);

public sealed record EnrollmentResponse(MachineId MachineId);

public sealed record AgentChallengeRequest(MachineId MachineId);

public sealed record AgentChallengeResponse(string Nonce, int ExpiresIn);

public sealed record AgentTokenRequest(MachineId MachineId, string Nonce, string Signature);

public sealed record AgentTokenResponse(string AccessToken, int ExpiresIn);

public sealed record EnrollmentCodeRequest(string? Name);

public sealed record EnrollmentCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record AgentHelloTicket(string MachineId, string ConnectionId, DateTimeOffset ExpiresAt);

public sealed record BrowserUserResponse(string Email, string? Name, string? AuthTime);

public static class EnrollmentCodeFormat
{
    public const int Length = 8;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate() => Generate(RandomNumberGenerator.GetInt32);

    public static string Generate(Func<int, int> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Span<char> buffer = stackalloc char[Length + 1];
        for (var i = 0; i < Length; i++)
        {
            buffer[i < 4 ? i : i + 1] = Alphabet[next(Alphabet.Length)];
        }

        buffer[4] = '-';
        return new string(buffer);
    }

    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }
}

public static class AgentTokenDefaults
{
    public const string Scheme = "AgentToken";

    public const string Audience = "agent";

    public const string Issuer = "aictrl";

    public const int LifetimeSeconds = 900;

    public const string MachineIdClaim = "mid";

    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(60);
}
