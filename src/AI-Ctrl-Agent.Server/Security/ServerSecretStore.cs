using System.Security.Cryptography;
using System.Text;

namespace AiCtrl.Server.Security;

public interface IServerSecretStore
{
    string? Get(string name);

    void Set(string name, string value);
}

public sealed class InMemoryServerSecretStore : IServerSecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string value) => _values[name] = value;
}

public sealed class DpapiServerSecretStore : IServerSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiCtrl.Server.Secrets.v1");

    private readonly string _path;

    private readonly Dictionary<string, string> _values;

    public DpapiServerSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _values = Load(path);
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
        Save();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plain = string.Join('\n', _values.Select(pair => pair.Key + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value))));
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, _path, overwrite: true);
    }

    private static Dictionary<string, string> Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine));
        foreach (var line in plain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('\t');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator]] = Encoding.UTF8.GetString(Convert.FromBase64String(line[(separator + 1)..]));
        }

        return values;
    }
}
