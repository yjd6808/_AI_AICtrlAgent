using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Commands;

public static class PermissionResponses
{
    public const string Once = "once";

    public const string Always = "always";

    public const string Reject = "reject";
}

public static class FileConflicts
{
    public const string Fail = "fail";

    public const string Overwrite = "overwrite";

    public const string Rename = "rename";
}

public sealed record SessionAbortPayload(SessionRef Session, bool IncludeChildren = true);

public sealed record SessionPromptPayload(SessionRef Session, string Text, ModelRef? Model, string? Agent);

public sealed record SessionAbortAndPromptPayload(SessionRef Session, string Text, ModelRef? Model);

public sealed record SessionCreatePayload(
    InstanceId Instance,
    string WorkspaceId,
    string? Title,
    string InitialPrompt,
    ModelRef? Model);

public sealed record SessionPermissionReplyPayload(SessionRef Session, string PermissionId, string Response);

public sealed record OpenCodeBackendRestartPayload(InstanceId Instance, bool Force);

public sealed record VpnConnectPayload(string? Server, string? Country, string? City);

public sealed record FilesReceivePayload(
    string TransferId,
    string WorkspaceId,
    string RelDir,
    string FileName,
    string Conflict,
    string? Sha256 = null);

public sealed record FilesSendPayload(string TransferId, string WorkspaceId, string RelPath, bool AsZip);

public sealed record FilesMkdirPayload(string WorkspaceId, string RelPath);

public sealed record FilesDeletePayload(string WorkspaceId, string RelPath);

public sealed record FilesRenamePayload(string WorkspaceId, string RelPath, string NewName);

public sealed record WorkspaceRegisterPayload(string Path);

public sealed record PowerKeepAwakePayload(bool Enabled, int? UntilMinutes);
