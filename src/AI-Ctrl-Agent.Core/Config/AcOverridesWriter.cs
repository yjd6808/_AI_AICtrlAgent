using System.Text;

namespace AiCtrl.Core.Config;

public sealed class AcOverridesWriter
{
    public AcOverridesWriter(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public string Write(
        IReadOnlyDictionary<string, string?> updates,
        IReadOnlyCollection<string>? removedKeys = null)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(FilePath))
        {
            var existing = AcYamlFlattener.Flatten(File.ReadAllText(FilePath, Encoding.UTF8), FilePath);
            foreach (var pair in existing.Values)
            {
                values[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in updates)
        {
            values[pair.Key] = pair.Value;
        }

        if (removedKeys is not null)
        {
            foreach (var key in removedKeys)
            {
                values.Remove(key);
            }
        }

        values[AcConfigValidator.ConfigVersionKey] = "1";

        var content = AcYamlSerializer.Serialize(values);
        var hash = AcYamlConfigurationProvider.ComputeHash(content);

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = FilePath + ".tmp";
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, utf8NoBom))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(FilePath))
        {
            File.Replace(tempPath, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, FilePath);
        }

        return hash;
    }
}
