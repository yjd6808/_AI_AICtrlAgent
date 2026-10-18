namespace AiCtrl.Core.Domain;

public sealed record WorkspaceInfo(
    string WorkspaceId,
    string Name,
    string Root,
    bool AllowUpload,
    bool AllowDownload,
    bool AllowMkdir,
    bool AllowDelete,
    IReadOnlyList<string> DenyPatterns,
    IReadOnlyList<SessionRef> LinkedSessions);

public sealed record FileEntry(
    string Name,
    string RelPath,
    bool IsDir,
    long? Size,
    DateTimeOffset Modified,
    bool Hidden,
    bool Denied,
    bool IsReparsePoint);
