using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Api;
using AiCtrl.Core.Workspace;

namespace AiCtrl.Client.Workspace;

public sealed class WorkspaceRegistry
{
    private readonly WorkspaceOptions _options;

    private readonly Dictionary<string, WorkspaceEntry> _entries;

    public WorkspaceRegistry(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _entries = new Dictionary<string, WorkspaceEntry>(StringComparer.Ordinal);
        foreach (var definition in options.Workspaces)
        {
            var entry = CreateEntry(definition);
            _entries.TryAdd(entry.WorkspaceId, entry);
        }
    }

    public IReadOnlyList<WorkspaceEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.Values.OrderBy(entry => entry.Definition.Name).ToList();
            }
        }
    }

    public WorkspaceEntry Register(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"Path '{path}' is not valid.");
        }

        if (!_options.IsUnderParent(root))
        {
            throw new WorkspaceException(
                AcErrorCodes.WorkspaceNotAllowed,
                $"Path '{root}' is not under a configured workspace parent.");
        }

        if (!Directory.Exists(root))
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"Directory '{root}' was not found.");
        }

        lock (_entries)
        {
            foreach (var existing in _entries.Values)
            {
                if (string.Equals(existing.Definition.Root, root, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }

            var name = Path.GetFileName(root);
            var entry = CreateEntry(new WorkspaceDefinition(
                string.IsNullOrWhiteSpace(name) ? root : name,
                root,
                AllowUpload: true,
                AllowDownload: true,
                AllowMkdir: true,
                AllowDelete: false,
                DenyPatterns: []));
            _entries.TryAdd(entry.WorkspaceId, entry);
            return entry;
        }
    }

    public WorkspaceEntry? Find(string workspaceId)
    {
        lock (_entries)
        {
            return _entries.GetValueOrDefault(workspaceId);
        }
    }

    public WorkspaceEntry Get(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return Find(workspaceId) ?? throw WorkspaceException.NotFound(workspaceId);
    }

    public static string CreateWorkspaceId(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "ws_" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static WorkspaceEntry CreateEntry(WorkspaceDefinition definition)
    {
        var denyPatterns = AiCtrl.Client.Workspace.WorkspaceOptions.MergeDenyPatterns(definition.DenyPatterns);
        var policy = new WorkspacePathPolicy(definition.Root, denyPatterns);
        return new WorkspaceEntry(
            CreateWorkspaceId(definition.Root),
            definition with { DenyPatterns = denyPatterns },
            policy);
    }
}

public sealed record WorkspaceEntry(
    string WorkspaceId,
    WorkspaceDefinition Definition,
    WorkspacePathPolicy Policy);
