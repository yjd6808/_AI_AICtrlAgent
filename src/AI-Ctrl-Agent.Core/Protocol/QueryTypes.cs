using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Protocol;

public static class QueryTypes
{
    public const string MachineSnapshot = "machine.snapshot";

    public const string MachineHealth = "machine.health";

    public const string MachineFeatures = "machine.features";

    public const string AgentInstances = "agent.instances";

    public const string SessionList = "session.list";

    public const string SessionGet = "session.get";

    public const string SessionMessages = "session.messages";

    public const string SessionDiff = "session.diff";

    public const string SessionModels = "session.models";

    public const string WorkspaceList = "workspace.list";

    public const string WorkspaceGit = "workspace.git";

    public const string FilesList = "files.list";

    public const string FilesStat = "files.stat";

    public const string FilesPreview = "files.preview";

    public const string LogsList = "logs.list";

    public const string LogsTail = "logs.tail";
}

public sealed record AgentInstancesQuery(string? Kind);

public sealed record SessionListQuery(
    string? Instance,
    string View,
    bool IncludeChildren,
    string? Cursor);

public sealed record SessionGetQuery(SessionRef Session);

public sealed record SessionMessagesQuery(
    SessionRef Session,
    string? Cursor,
    int Limit);

public sealed record SessionModelsQuery(string? Instance);

public sealed record SessionDiffQuery(SessionRef Session);

public sealed record WorkspaceListQuery();

public sealed record WorkspaceGitQuery(string Workspace);

public sealed record LogsListQuery(string Scope);

public sealed record LogsTailQuery(
    string Scope,
    string Name,
    int Lines);
