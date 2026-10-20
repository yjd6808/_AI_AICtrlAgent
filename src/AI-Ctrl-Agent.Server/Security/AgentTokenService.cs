using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCtrl.Core.Protocol;

namespace AiCtrl.Server.Security;

public sealed record AgentTokenValidation(bool Valid, string? MachineId, DateTimeOffset ExpiresAt);

public sealed class AgentTokenService
{
    private const string SecretName = "agent-token-key";

    private static readonly byte[] Header = Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}""");

    private readonly byte[] _key;

    private readonly TimeProvider _time;

    public AgentTokenService(IServerSecretStore secrets, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        var encoded = secrets.Get(SecretName);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            _key = RandomNumberGenerator.GetBytes(32);
            secrets.Set(SecretName, Convert.ToBase64String(_key));
        }
        else
        {
            _key = Convert.FromBase64String(encoded);
        }
    }

    public string Create(string machineId, out DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        var now = _time.GetUtcNow();
        expiresAt = now.AddSeconds(AgentTokenDefaults.LifetimeSeconds);

        var header = Base64Url(Header);
        var payload = Base64Url(BuildPayload(machineId, now, expiresAt));
        var signature = Sign(header + "." + payload);
        return header + "." + payload + "." + signature;
    }

    public AgentTokenValidation Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AgentTokenValidation(false, null, DateTimeOffset.MinValue);
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return new AgentTokenValidation(false, null, DateTimeOffset.MinValue);
        }

        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var algorithm)
                || !string.Equals(algorithm.GetString(), "HS256", StringComparison.Ordinal)
                || !header.RootElement.TryGetProperty("typ", out var type)
                || !string.Equals(type.GetString(), "JWT", StringComparison.Ordinal))
            {
                return new AgentTokenValidation(false, null, DateTimeOffset.MinValue);
            }

            var expected = SignBytes(parts[0] + "." + parts[1]);
            var actual = Base64UrlDecode(parts[2]);
            if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return new AgentTokenValidation(false, null, DateTimeOffset.MinValue);
            }

            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = payload.RootElement;
            if (!root.TryGetProperty("aud", out var audience)
                || !string.Equals(audience.GetString(), AgentTokenDefaults.Audience, StringComparison.Ordinal)
                || !root.TryGetProperty("iss", out var issuer)
                || !string.Equals(issuer.GetString(), AgentTokenDefaults.Issuer, StringComparison.Ordinal)
                || !root.TryGetProperty("exp", out var exp)
                || !root.TryGetProperty(AgentTokenDefaults.MachineIdClaim, out var machine))
            {
                return new AgentTokenValidation(false, null, DateTimeOffset.MinValue);
            }

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
            if (expiresAt <= _time.GetUtcNow())
            {
                return new AgentTokenValidation(false, null, expiresAt);
            }

            return new AgentTokenValidation(true, machine.GetString(), expiresAt);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return new AgentTokenValidation(false, null, DateTimeOffset.MinValue);
        }
    }

    private string Sign(string value) => Base64Url(SignBytes(value));

    private byte[] SignBytes(string value)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(value));
    }

    private byte[] BuildPayload(string machineId, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("iss", AgentTokenDefaults.Issuer);
            writer.WriteString("aud", AgentTokenDefaults.Audience);
            writer.WriteString(AgentTokenDefaults.MachineIdClaim, machineId);
            writer.WriteNumber("iat", issuedAt.ToUnixTimeSeconds());
            writer.WriteNumber("exp", expiresAt.ToUnixTimeSeconds());
            writer.WriteString("jti", Guid.NewGuid().ToString("N"));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }
}
