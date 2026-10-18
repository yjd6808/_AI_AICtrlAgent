using System.Collections;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Core.Config;

public sealed class AcEnvironmentVariableProvider : ConfigurationProvider
{
    public AcEnvironmentVariableProvider(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        Prefix = prefix;
    }

    public string Prefix { get; }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var variables = Environment.GetEnvironmentVariables();

        foreach (DictionaryEntry entry in variables)
        {
            var name = entry.Key as string;
            if (name is null || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = name[Prefix.Length..].Replace("__", ":", StringComparison.Ordinal).ToLowerInvariant();
            if (key.Length == 0)
            {
                continue;
            }

            data[key] = entry.Value as string;
        }

        Data = data;
    }
}
