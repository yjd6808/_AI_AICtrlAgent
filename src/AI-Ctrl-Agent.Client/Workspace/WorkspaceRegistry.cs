using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Api;
using AiCtrl.Core.Workspace;

namespace AiCtrl.Client.Workspace;

public sealed class WorkspaceRegistry
{
    private readonly Dictionary<string, WorkspaceEntry> _entries;

    public WorkspaceRegistry(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _entries = new Dictionary<string, WorkspaceEntry>(StringComparer.Ordinal);
        foreach (var definition in options.Workspaces)
        {
            var entry = CreateEntry(definition);
            _entries.TryAdd(entry.WorkspaceId, entry);
        }
    }

    public IReadOnlyList<WorkspaceEntry> Entries => _entries.Values.OrderBy(entry => entry.Definition.Name).ToList();

    public WorkspaceEntry? Find(string workspaceId) =>
        _entries.GetValueOrDefault(workspaceId);

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
