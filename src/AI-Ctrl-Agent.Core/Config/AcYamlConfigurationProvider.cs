using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Config;

public sealed class AcYamlConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly bool _watch;
    private readonly TimeSpan _reloadDebounce;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private string? _contentHash;
    private bool _disposed;

    public AcYamlConfigurationProvider(
        string filePath,
        bool optional,
        bool watch = false,
        TimeSpan? reloadDebounce = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
        Optional = optional;
        _watch = watch;
        _reloadDebounce = reloadDebounce ?? TimeSpan.FromMilliseconds(500);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public string FilePath { get; }

    public bool Optional { get; }

    public IReadOnlyDictionary<string, int> KeyLines { get; private set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string? ContentHash => _contentHash;

    public Exception? LastReloadError { get; private set; }

    public static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public override void Load()
    {
        if (!File.Exists(FilePath))
        {
            if (!Optional)
            {
                throw new AcConfigException(FilePath, 0, 0, "설정 파일이 없습니다.");
            }

            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            KeyLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            EnsureWatcher();
            return;
        }

        var text = File.ReadAllText(FilePath, Encoding.UTF8);
        var document = AcYamlFlattener.Flatten(text, FilePath);
        Data = document.Values;
        KeyLines = document.KeyLines;
        _contentHash = ComputeHash(text);
        LastReloadError = null;
        EnsureWatcher();
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;

        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void EnsureWatcher()
    {
        if (!_watch || _watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnWatcherEvent;
        _watcher.Created += OnWatcherEvent;
        _watcher.Renamed += OnWatcherEvent;
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _debounceTimer ??= _timeProvider.CreateTimer(
                _ => ReloadFromWatcher(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(_reloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadFromWatcher()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var text = File.ReadAllText(FilePath, Encoding.UTF8);
            var hash = ComputeHash(text);
            if (hash == _contentHash)
            {
                return;
            }

            var document = AcYamlFlattener.Flatten(text, FilePath);
            Data = document.Values;
            KeyLines = document.KeyLines;
            _contentHash = hash;
            LastReloadError = null;
            OnReload();
        }
        catch (Exception ex) when (ex is AcConfigException or IOException or UnauthorizedAccessException)
        {
            LastReloadError = ex;
            _logger?.LogWarning(ex, "설정 파일 다시 로드 실패: {Path}", FilePath);
        }
    }
}
