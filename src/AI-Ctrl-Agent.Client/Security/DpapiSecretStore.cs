using System.Security.Cryptography;
using System.Text;
using AiCtrl.Client.Providers.OpenCode.Backend;

namespace AiCtrl.Client.Security;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _path;

    private readonly object _gate = new();

    public DpapiSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public ValueTask<string?> LoadAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var values = ReadAll();
            return ValueTask.FromResult(values.GetValueOrDefault(key));
        }
    }

    public ValueTask SaveAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var values = ReadAll();
            values[key] = value;
            WriteAll(values);
        }

        return ValueTask.CompletedTask;
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            try
            {
                var encrypted = Convert.FromBase64String(parts[1]);
                var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                values[parts[0]] = Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                continue;
            }
        }

        return values;
    }

    private void WriteAll(Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = values.Select(pair =>
        {
            var plain = Encoding.UTF8.GetBytes(pair.Value);
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            return pair.Key + "\t" + Convert.ToBase64String(encrypted);
        });
        var temp = _path + ".tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, _path, overwrite: true);
    }
}
