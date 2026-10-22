using System.Globalization;
using AiCtrl.Core.Workspace;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Workspace;

public sealed record WorkspaceOptions(
    IReadOnlyList<WorkspaceDefinition> Workspaces,
    IReadOnlyList<string> Parents)
{
    public static WorkspaceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var workspaces = new List<WorkspaceDefinition>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in configuration.GetSection("workspaces").GetChildren())
        {
            var rootValue = section["root"];
            if (string.IsNullOrWhiteSpace(rootValue))
            {
                continue;
            }

            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!roots.Add(root))
            {
                continue;
            }

            var name = section["name"];
            workspaces.Add(new WorkspaceDefinition(
                string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root) : name,
                root,
                ReadBool(section, "allowUpload", true),
                ReadBool(section, "allowDownload", true),
                ReadBool(section, "allowMkdir", true),
                ReadBool(section, "allowDelete", false),
                ReadList(section.GetSection("denyPatterns"))));
        }

        return new WorkspaceOptions(workspaces, ReadList(configuration.GetSection("workspaceParents")));
    }

    public bool IsUnderParent(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var parent in Parents)
        {
            string fullParent;
            try
            {
                fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> MergeDenyPatterns(IReadOnlyList<string> patterns)
    {
        var merged = new List<string>(WorkspacePathPolicy.DefaultDenyPatterns);
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern)
                && !merged.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(pattern);
            }
        }

        return merged;
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;

    private static List<string> ReadList(IConfigurationSection section)
    {
        var values = new List<string>();
        foreach (var child in section.GetChildren())
        {
            var value = child.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                values.Add(value.Trim());
                continue;
            }

            values.Add(value.Trim());
        }

        return values;
    }
}
