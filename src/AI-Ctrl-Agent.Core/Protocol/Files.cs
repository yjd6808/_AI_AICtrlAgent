using AiCtrl.Core.Domain;

namespace AiCtrl.Core.Protocol;

public static class FileSorts
{
    public const string Name = "name";

    public const string Modified = "modified";

    public const string Size = "size";
}

public sealed record FileListQuery(
    string WorkspaceId,
    string? Path,
    string Sort,
    string? Cursor,
    int Limit);

public sealed record FileListPage(
    List<FileEntry> Entries,
    string? NextCursor,
    bool HasMore);

public sealed record FileStatQuery(string WorkspaceId, string Path, bool Hash);

public sealed record FileStatView(
    string Name,
    string RelPath,
    bool IsDir,
    long? Size,
    DateTimeOffset Modified,
    bool Hidden,
    bool Denied,
    bool IsReparsePoint,
    string? Sha256);

public sealed record FileMutationResult(string WorkspaceId, string RelPath, bool IsDir);

public sealed record FilePreviewQuery(string WorkspaceId, string Path);

public sealed record WorkspaceRegisterRequest(string Path);

public sealed record FilePreviewView(
    string Name,
    string RelPath,
    string Kind,
    long Size,
    DateTimeOffset Modified,
    string? Text,
    string? Base64,
    bool Truncated);

public sealed record FsBrowseQuery(string? Path);

public sealed record FsDriveView(
    string Path,
    string Name,
    string? Label,
    bool Ready,
    long? TotalBytes,
    long? FreeBytes);

public sealed record FsEntryView(
    string Name,
    string FullPath,
    bool IsDir,
    bool Denied,
    bool IsReparsePoint);

public sealed record FsQuickPathView(
    string Key,
    string Path);

public sealed record FsBrowseView(
    string? Path,
    string? ParentPath,
    List<FsDriveView> Drives,
    List<FsEntryView> Entries,
    List<FsQuickPathView> QuickPaths);
