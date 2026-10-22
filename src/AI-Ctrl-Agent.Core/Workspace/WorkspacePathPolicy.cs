using System.Text;
using System.Text.RegularExpressions;
using AiCtrl.Core.Api;

namespace AiCtrl.Core.Workspace;

public sealed class WorkspacePathException : Exception
{
    public WorkspacePathException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record WorkspacePath(string RelativePath, string FullPath);

public sealed class WorkspacePathPolicy
{
    public static readonly IReadOnlyList<string> DefaultDenyPatterns =
    [
        "**/.env",
        "**/.env.*",
        "**/*.pem",
        "**/*.pfx",
        "**/*.key",
        "**/id_rsa*",
        "**/.git/config",
        "**/secrets.*",
    ];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    };

    private readonly string _root;

    private readonly string _rootPrefix;

    private readonly string[] _hardDenyPaths;

    private readonly Regex[] _denyPatterns;

    public WorkspacePathPolicy(
        string root,
        IEnumerable<string>? denyPatterns = null,
        IEnumerable<string>? hardDenyPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        _hardDenyPaths = (hardDenyPaths ?? CreateDefaultHardDenyPaths())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _denyPatterns = (denyPatterns ?? DefaultDenyPatterns)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(CompilePattern)
            .ToArray();
    }

    public string Root => _root;

    public WorkspacePath Resolve(string? relativePath)
    {
        var parts = Split(relativePath);
        var canonical = string.Join('/', parts);
        var candidate = _root;
        if (parts.Count > 0)
        {
            candidate = Path.GetFullPath(Path.Join(_root, string.Join(Path.DirectorySeparatorChar, parts)));
        }

        EnsureInside(candidate);

        var current = _root;
        for (var index = 0; index < parts.Count; index++)
        {
            current = Path.Join(current, parts[index]);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info is null)
            {
                for (var remaining = index + 1; remaining < parts.Count; remaining++)
                {
                    current = Path.Join(current, parts[remaining]);
                }

                break;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WorkspacePathException(
                    AcErrorCodes.PathDenied,
                    "The workspace path crosses an unreadable link.");
            }

            if (target is null)
            {
                throw new WorkspacePathException(
                    AcErrorCodes.PathDenied,
                    "The workspace path crosses an unresolved link.");
            }

            current = Path.GetFullPath(target.FullName);
            EnsureInside(current);
        }

        EnsureNoHardDeny(candidate);
        EnsureNoHardDeny(current);
        return new WorkspacePath(canonical, current);
    }

    public bool IsDenied(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        return _denyPatterns.Any(pattern => pattern.IsMatch(normalized));
    }

    public static IReadOnlyList<string> CreateDefaultHardDenyPaths()
    {
        var paths = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            paths.Add(Path.Combine(localAppData, "AiCtrl"));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            paths.Add(Path.Combine(profile, ".local", "share", "opencode", "auth.json"));
        }

        return paths;
    }

    public static bool IsValidFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var normalized = fileName.Replace('\\', '/');
        return !normalized.Contains('/') && IsValidComponent(normalized);
    }

    private static List<string> Split(string? relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return [];
        }

        if (normalized[0] == '/'
            || normalized.Contains(':')
            || normalized.Contains("//", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized))
        {
            throw Invalid("The path must be relative to the workspace root.");
        }

        var parts = normalized.Split('/').ToList();
        foreach (var part in parts)
        {
            if (!IsValidComponent(part))
            {
                throw Invalid($"The path component '{part}' is not allowed.");
            }
        }

        return parts;
    }

    private static bool IsValidComponent(string component)
    {
        if (component.Length == 0
            || component is "." or ".."
            || component.EndsWith('.')
            || component.EndsWith(' ')
            || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || component.Any(char.IsControl))
        {
            return false;
        }

        var deviceName = component.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(deviceName);
    }

    private void EnsureInside(string path)
    {
        if (string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new WorkspacePathException(
            AcErrorCodes.PathDenied,
            "The path resolves outside the workspace root.");
    }

    private void EnsureNoHardDeny(string path)
    {
        foreach (var denied in _hardDenyPaths)
        {
            if (string.Equals(path, denied, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(denied + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspacePathException(
                    AcErrorCodes.PathDenied,
                    "The path is always denied.");
            }
        }
    }

    private static Regex CompilePattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/').TrimStart('/');
        var builder = new StringBuilder("^");
        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (current == '*')
            {
                var isDouble = index + 1 < normalized.Length && normalized[index + 1] == '*';
                if (isDouble)
                {
                    index++;
                    if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            if (current == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            builder.Append(Regex.Escape(current.ToString()));
        }

        builder.Append('$');
        return new Regex(
            builder.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
    }

    private static WorkspacePathException Invalid(string message) =>
        new(AcErrorCodes.PathInvalid, message);
}
