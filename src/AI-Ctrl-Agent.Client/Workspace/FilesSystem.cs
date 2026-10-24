using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Workspace;

namespace AiCtrl.Client.Workspace;

public sealed class FilesSystem
{
    private const int MaxListLimit = 500;

    private const int MaxPreviewBytes = 1024 * 1024;

    private const int MaxImageBytes = 5 * 1024 * 1024;

    private const long MaxHashBytes = 512L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".bmp",
        ".ico",
    };

    private readonly WorkspaceRegistry _workspaces;

    private readonly string[] _hardDenyPaths;

    public FilesSystem(WorkspaceRegistry workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        _workspaces = workspaces;
        _hardDenyPaths = WorkspacePathPolicy.CreateDefaultHardDenyPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public FsBrowseView Browse(FsBrowseQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Path))
        {
            return new FsBrowseView(null, null, GetDrives(), [], GetQuickPaths());
        }

        var fullPath = ResolveDirectory(query.Path);
        var entries = new List<FsEntryView>();
        try
        {
            foreach (var info in new DirectoryInfo(fullPath).EnumerateDirectories())
            {
                entries.Add(new FsEntryView(
                    info.Name,
                    Path.TrimEndingDirectorySeparator(info.FullName),
                    IsDir: true,
                    IsDeniedPath(info.FullName),
                    (info.Attributes & FileAttributes.ReparsePoint) != 0));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 하위 폴더를 읽을 수 없으면 빈 목록을 반환합니다.
        }
        catch (IOException)
        {
            // 이동식 드라이브 분리 등으로 열거가 중단되면 지금까지 수집한 항목만 반환합니다.
        }

        entries.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return new FsBrowseView(
            fullPath,
            Directory.GetParent(fullPath)?.FullName is { } parent
                ? Path.TrimEndingDirectorySeparator(parent)
                : null,
            [],
            entries,
            []);
    }

    public string ResolveSessionDirectory(string path)
    {
        var fullPath = ResolveDirectory(path);
        if (!IsDeniedPath(fullPath))
        {
            return fullPath;
        }

        throw new WorkspaceException(AcErrorCodes.PathDenied, "The directory is always denied.");
    }

    public IReadOnlyList<WorkspaceInfo> GetWorkspaceInfos(IEnumerable<SessionSummary> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var allSessions = sessions.ToList();
        return _workspaces.Entries
            .Select(entry => new WorkspaceInfo(
                entry.WorkspaceId,
                entry.Definition.Name,
                entry.Definition.Root,
                entry.Definition.AllowUpload,
                entry.Definition.AllowDownload,
                entry.Definition.AllowMkdir,
                entry.Definition.AllowDelete,
                entry.Definition.DenyPatterns,
                allSessions
                    .Where(session => IsUnder(session.Directory, entry.Definition.Root))
                    .Select(session => session.Ref)
                    .ToList()))
            .ToList();
    }

    public WorkspaceInfo GetWorkspaceInfo(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new WorkspaceInfo(
            entry.WorkspaceId,
            entry.Definition.Name,
            entry.Definition.Root,
            entry.Definition.AllowUpload,
            entry.Definition.AllowDownload,
            entry.Definition.AllowMkdir,
            entry.Definition.AllowDelete,
            entry.Definition.DenyPatterns,
            []);
    }

    public WorkspaceEntry GetWorkspace(string workspaceId) => _workspaces.Get(workspaceId);

    public WorkspaceEntry Register(string path) => _workspaces.Register(path);

    public FileListPage List(FileListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entry = _workspaces.Get(query.WorkspaceId);
        var path = entry.Policy.Resolve(query.Path);
        if (!Directory.Exists(path.FullPath))
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"Directory '{query.Path}' was not found.");
        }

        var children = new List<FileEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(path.FullPath).EnumerateFileSystemInfos())
            {
                var relative = path.RelativePath.Length == 0
                    ? info.Name
                    : path.RelativePath + "/" + info.Name;
                var isDir = info is DirectoryInfo;
                children.Add(new FileEntry(
                    info.Name,
                    relative,
                    isDir,
                    isDir ? (long?)null : ((FileInfo)info).Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    (info.Attributes & FileAttributes.Hidden) != 0,
                    entry.Policy.IsDenied(relative),
                    (info.Attributes & FileAttributes.ReparsePoint) != 0));
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new WorkspaceException(AcErrorCodes.PathDenied, "The directory cannot be read.");
        }
        catch (IOException ex)
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, ex.Message);
        }

        var sort = query.Sort is FileSorts.Modified or FileSorts.Size ? query.Sort : FileSorts.Name;
        children.Sort((left, right) => Compare(left, right, sort));

        var offset = ParseCursor(query.Cursor);
        var limit = Math.Clamp(query.Limit <= 0 ? MaxListLimit : query.Limit, 1, MaxListLimit);
        var page = children.Skip(offset).Take(limit).ToList();
        var hasMore = offset + page.Count < children.Count;
        return new FileListPage(
            page,
            hasMore ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            hasMore);
    }

    public async Task<FileStatView> StatAsync(FileStatQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entry = _workspaces.Get(query.WorkspaceId);
        var path = entry.Policy.Resolve(query.Path);
        var info = GetInfo(path.FullPath)
            ?? throw new WorkspaceException(AcErrorCodes.PathInvalid, $"Path '{query.Path}' was not found.");
        var isDir = info is DirectoryInfo;
        long? size = isDir ? null : ((FileInfo)info).Length;
        string? hash = null;
        if (query.Hash && size is <= MaxHashBytes)
        {
            try
            {
                await using var stream = new FileStream(
                    path.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))
                    .ToLowerInvariant();
            }
            catch (IOException ex)
            {
                throw new WorkspaceException(AcErrorCodes.PathInvalid, ex.Message);
            }
        }

        return new FileStatView(
            info.Name,
            path.RelativePath,
            isDir,
            size,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            (info.Attributes & FileAttributes.Hidden) != 0,
            entry.Policy.IsDenied(path.RelativePath),
            (info.Attributes & FileAttributes.ReparsePoint) != 0,
            hash);
    }

    public FilePreviewView Preview(FilePreviewQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entry = _workspaces.Get(query.WorkspaceId);
        var path = entry.Policy.Resolve(query.Path);
        if (entry.Policy.IsDenied(path.RelativePath))
        {
            throw new WorkspaceException(AcErrorCodes.PathDenied, "The path is denied by workspace policy.");
        }

        var info = new FileInfo(path.FullPath);
        if (!info.Exists)
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"File '{query.Path}' was not found.");
        }

        if (info.Length > MaxPreviewBytes && !ImageExtensions.Contains(info.Extension))
        {
            var prefix = ReadPrefix(path.FullPath, MaxPreviewBytes);
            return new FilePreviewView(
                info.Name,
                path.RelativePath,
                "binary",
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                null,
                null,
                Truncated: true);
        }

        var bytes = ReadPrefix(path.FullPath, MaxPreviewBytes);
        if (ImageExtensions.Contains(info.Extension) && info.Length <= MaxImageBytes)
        {
            return new FilePreviewView(
                info.Name,
                path.RelativePath,
                "image",
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                null,
                Convert.ToBase64String(File.ReadAllBytes(path.FullPath)),
                Truncated: false);
        }

        if (LooksBinary(bytes))
        {
            return new FilePreviewView(
                info.Name,
                path.RelativePath,
                "binary",
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                null,
                null,
                info.Length > bytes.Length);
        }

        return new FilePreviewView(
            info.Name,
            path.RelativePath,
            "text",
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            DecodeText(bytes),
            null,
            info.Length > bytes.Length);
    }

    public FileStatView Mkdir(FilesMkdirPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var entry = _workspaces.Get(payload.WorkspaceId);
        EnsureAllowed(entry.Definition.AllowMkdir, "Directory creation is disabled for this workspace.");
        var path = entry.Policy.Resolve(payload.RelPath);
        if (File.Exists(path.FullPath))
        {
            throw new WorkspaceException(AcErrorCodes.FileConflict, "A file with the same name already exists.");
        }

        Directory.CreateDirectory(path.FullPath);
        return ToStat(entry, path);
    }

    public FileMutationResult Delete(FilesDeletePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var entry = _workspaces.Get(payload.WorkspaceId);
        EnsureAllowed(entry.Definition.AllowDelete, "Deleting is disabled for this workspace.");
        var path = entry.Policy.Resolve(payload.RelPath);
        if (path.FullPath == entry.Policy.Root)
        {
            throw new WorkspaceException(AcErrorCodes.PathDenied, "The workspace root cannot be deleted.");
        }

        var isDir = Directory.Exists(path.FullPath);
        if (isDir || File.Exists(path.FullPath))
        {
            RecycleBin.Delete(path.FullPath);
            return new FileMutationResult(payload.WorkspaceId, path.RelativePath, isDir);
        }

        throw new WorkspaceException(AcErrorCodes.PathInvalid, "The path was not found.");
    }

    public FileStatView Rename(FilesRenamePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var entry = _workspaces.Get(payload.WorkspaceId);
        EnsureAllowed(entry.Definition.AllowDelete, "Renaming is disabled for this workspace.");
        if (!WorkspacePathPolicy.IsValidFileName(payload.NewName))
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, "The new name is not a valid file name.");
        }

        var path = entry.Policy.Resolve(payload.RelPath);
        if (path.FullPath == entry.Policy.Root)
        {
            throw new WorkspaceException(AcErrorCodes.PathDenied, "The workspace root cannot be renamed.");
        }

        var parentRelative = path.RelativePath.Contains('/')
            ? path.RelativePath[..path.RelativePath.LastIndexOf('/')]
            : string.Empty;
        var newRelative = parentRelative.Length == 0
            ? payload.NewName
            : parentRelative + "/" + payload.NewName;
        var destination = entry.Policy.Resolve(newRelative);
        if (Directory.Exists(destination.FullPath) || File.Exists(destination.FullPath))
        {
            throw new WorkspaceException(AcErrorCodes.FileConflict, "The destination already exists.");
        }

        if (Directory.Exists(path.FullPath))
        {
            Directory.Move(path.FullPath, destination.FullPath);
        }
        else if (File.Exists(path.FullPath))
        {
            File.Move(path.FullPath, destination.FullPath);
        }
        else
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, "The path was not found.");
        }

        return ToStat(entry, destination);
    }

    private static FileStatView ToStat(WorkspaceEntry entry, WorkspacePath path)
    {
        var info = GetInfo(path.FullPath)
            ?? throw new WorkspaceException(AcErrorCodes.PathInvalid, "The path was not found.");
        return new FileStatView(
            info.Name,
            path.RelativePath,
            info is DirectoryInfo,
            info is DirectoryInfo ? (long?)null : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            (info.Attributes & FileAttributes.Hidden) != 0,
            entry.Policy.IsDenied(path.RelativePath),
            (info.Attributes & FileAttributes.ReparsePoint) != 0,
            null);
    }

    private static FileSystemInfo? GetInfo(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return new DirectoryInfo(fullPath);
        }

        return File.Exists(fullPath) ? new FileInfo(fullPath) : null;
    }

    private static int Compare(FileEntry left, FileEntry right, string sort)
    {
        if (left.IsDir != right.IsDir)
        {
            return left.IsDir ? -1 : 1;
        }

        var result = sort switch
        {
            FileSorts.Modified => right.Modified.CompareTo(left.Modified),
            FileSorts.Size => (right.Size ?? 0).CompareTo(left.Size ?? 0),
            _ => 0,
        };

        return result != 0
            ? result
            : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        return int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset)
            && offset >= 0
                ? offset
                : throw new WorkspaceException(AcErrorCodes.InvalidPayload, "The list cursor is invalid.");
    }

    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var limit = Math.Min(bytes.Length, 8192);
        for (var index = 0; index < limit; index++)
        {
            if (bytes[index] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    private static byte[] ReadPrefix(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        var length = (int)Math.Min(stream.Length, maxBytes);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static bool IsUnder(string? candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void EnsureAllowed(bool allowed, string message)
    {
        if (!allowed)
        {
            throw new WorkspaceException(AcErrorCodes.WorkspaceNotAllowed, message);
        }
    }

    private List<FsQuickPathView> GetQuickPaths()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (string Key, string? Path)[]
        {
            ("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("downloads", string.IsNullOrWhiteSpace(userProfile) ? null : Path.Combine(userProfile, "Downloads")),
            ("profile", userProfile),
            ("appdata", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("localappdata", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        };

        var quickPaths = new List<FsQuickPathView>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, path) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsDeniedPath(fullPath) || !seen.Add(fullPath))
            {
                continue;
            }

            quickPaths.Add(new FsQuickPathView(key, fullPath));
        }

        return quickPaths;
    }

    private static List<FsDriveView> GetDrives()
    {
        var drives = new List<FsDriveView>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var ready = false;
            string? label = null;
            long? total = null;
            long? free = null;
            try
            {
                ready = drive.IsReady;
                if (ready)
                {
                    label = drive.VolumeLabel;
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }

            drives.Add(new FsDriveView(
                Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName),
                drive.Name,
                string.IsNullOrWhiteSpace(label) ? null : label,
                ready,
                total,
                free));
        }

        drives.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return drives;
    }

    private string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, "A directory path is required.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"'{path}' is not an absolute path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"'{path}' is not a valid path.");
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new WorkspaceException(AcErrorCodes.PathInvalid, $"'{path}' is not a valid path.");
        }

        var current = Path.GetFullPath(root);
        EnsureNotDenied(current);
        var parts = fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = Path.Join(current, part);
            if (!Directory.Exists(current))
            {
                throw new WorkspaceException(
                    AcErrorCodes.PathInvalid,
                    $"Directory '{path}' was not found.");
            }

            current = ResolveLinkTarget(current);
            EnsureNotDenied(current);
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string ResolveLinkTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return path;
        }

        FileSystemInfo? target;
        try
        {
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException(AcErrorCodes.PathDenied, "The path crosses an unreadable link.");
        }

        return target is null
            ? throw new WorkspaceException(AcErrorCodes.PathDenied, "The path crosses an unresolved link.")
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
    }

    private bool IsDeniedPath(string path)
    {
        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        foreach (var denied in _hardDenyPaths)
        {
            if (string.Equals(normalized, denied, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(denied + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureNotDenied(string path)
    {
        if (IsDeniedPath(path))
        {
            throw new WorkspaceException(AcErrorCodes.PathDenied, "The directory is always denied.");
        }
    }
}
