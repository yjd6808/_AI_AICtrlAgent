using System.Security.Cryptography;
using System.Text;
using AiCtrl.Client.Providers.OpenCode.Backend;

namespace AiCtrl.Client.Security;

public sealed class DeviceKey : IDisposable
{
    private readonly ECDsa _key;

    internal DeviceKey(ECDsa key)
    {
        _key = key;
    }

    public string PublicKey =>
        Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public string PrivateKey =>
        Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

    public string Sign(string machineId, string nonce)
    {
        var payload = Encoding.UTF8.GetBytes($"aictrl-agent-v1|{machineId}|{nonce}");
        return Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256));
    }

    public void Dispose() => _key.Dispose();

    internal static DeviceKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    internal static DeviceKey FromPrivateKey(string base64)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
            return new DeviceKey(ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }
}

public sealed class DeviceKeyStore
{
    public const string SecretName = "agent.device.key";

    private readonly ISecretStore _secrets;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DeviceKey? _cached;

    public DeviceKeyStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public async Task<DeviceKey> GetOrCreateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var stored = await _secrets.LoadAsync(SecretName, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                _cached = DeviceKey.FromPrivateKey(stored);
                return _cached;
            }

            var created = DeviceKey.Create();
            await _secrets.SaveAsync(SecretName, created.PrivateKey, ct).ConfigureAwait(false);
            _cached = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceKey?> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var stored = await _secrets.LoadAsync(SecretName, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stored))
            {
                return null;
            }

            _cached = DeviceKey.FromPrivateKey(stored);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
