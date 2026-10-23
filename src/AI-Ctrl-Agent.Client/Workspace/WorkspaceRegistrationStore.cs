using System.Globalization;
using AiCtrl.Core.Config;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Workspace;

public sealed class WorkspaceRegistrationStore
{
    private readonly AcOverridesWriter _writer;

    public WorkspaceRegistrationStore(string overridesPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(overridesPath);
        _writer = new AcOverridesWriter(overridesPath);
    }

    public string FilePath => _writer.FilePath;

    public void Add(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var configuration = new ConfigurationBuilder()
            .AddAcYamlFile(_writer.FilePath, optional: true)
            .Build();
        var section = configuration.GetSection("registeredWorkspaces");
        var next = 0;
        foreach (var child in section.GetChildren())
        {
            if (int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= next)
            {
                next = index + 1;
            }

            var root = child["root"];
            if (!string.IsNullOrWhiteSpace(root)
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(root),
                    entry.Definition.Root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        _writer.Write(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"registeredWorkspaces:{next}:name"] = entry.Definition.Name,
            [$"registeredWorkspaces:{next}:root"] = entry.Definition.Root,
        });
    }
}
