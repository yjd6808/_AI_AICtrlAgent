using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Logging;

public sealed partial class AcLogService : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly AcLogWriter _writer;
    private int _shutdownRequested;

    public AcLogService(string appName, AcLogOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);
        ArgumentNullException.ThrowIfNull(options);

        _time = timeProvider ?? TimeProvider.System;
        Options = options;

        var directory = options.Directory ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        ApplyRetention(directory, options);

        var now = _time.GetLocalNow();
        var baseName = $"{now:yyyyMMdd}-{now:HHmmss}-{appName}";
        if (File.Exists(Path.Combine(directory, baseName + ".txt")))
        {
            baseName += "-" + Environment.ProcessId;
        }

        var candidate = baseName;
        var suffix = 2;
        while (File.Exists(Path.Combine(directory, candidate + ".txt")))
        {
            candidate = $"{baseName}-{suffix++}";
        }

        BaseName = candidate;
        FilePath = Path.Combine(directory, candidate + ".txt");
        _writer = new AcLogWriter(directory, candidate, options.MaxFileBytes);

        RawWrite(BuildStartHeader(appName));
        RawWrite("==== args: " + AcLogRedactor.Redact(string.Join(' ', Environment.GetCommandLineArgs())));
    }

    public AcLogOptions Options { get; }

    public string BaseName { get; }

    public string FilePath { get; }

    public long DroppedCount => _writer.Dropped;

    public AcFileLoggerProvider CreateProvider() => new(this);

    public void Write(LogLevel level, string category, string message, Exception? exception = null, string scopeSuffix = "")
    {
        if (level == LogLevel.None || level < Options.FileLevel)
        {
            return;
        }

        var line = AcLogFormatter.ComposeLine(
            _time.GetLocalNow(),
            level,
            Environment.CurrentManagedThreadId,
            category,
            message,
            scopeSuffix,
            exception);
        _writer.Post(level, AcLogRedactor.Redact(line));
    }

    public void WriteRawHeader(string line) => RawWrite(line);

    public void AppendConfigSummary(string summary)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            RawWrite("==== config: " + AcLogRedactor.Redact(summary));
        }
    }

    public void CrashWrite(LogLevel level, string category, string message, Exception? exception = null)
    {
        var line = AcLogFormatter.ComposeLine(
            _time.GetLocalNow(),
            level,
            Environment.CurrentManagedThreadId,
            category,
            message,
            string.Empty,
            exception);
        _writer.RawWrite(AcLogRedactor.Redact(line));
    }

    public void CrashShutdown(int exitCode) => _writer.CrashShutdown(BuildStopLine(exitCode));

    public void Shutdown(int exitCode)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
        {
            return;
        }

        _writer.Post(LogLevel.Information, AcLogRedactor.Redact(BuildStopLine(exitCode)));
        _writer.CompleteAndJoin(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => Shutdown(0);

    private void RawWrite(string line) => _writer.RawWrite(line);

    private string BuildStopLine(int exitCode)
    {
        var elapsed = _uptime.Elapsed;
        var uptime = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        return $"==== STOP exitCode={exitCode} uptime={uptime} dropped={_writer.Dropped}";
    }

    private static string BuildStartHeader(string appName)
    {
        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "0.0.0";

        return $"==== START {appName} {version} pid={Environment.ProcessId} user={Environment.UserDomainName}\\{Environment.UserName} host={Environment.MachineName} os={RuntimeInformation.OSDescription} .NET={RuntimeInformation.FrameworkDescription}";
    }

    private void ApplyRetention(string directory, AcLogOptions options)
    {
        try
        {
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.txt"))
            {
                var name = Path.GetFileName(path);
                if (LogFileNameRegex().IsMatch(name))
                {
                    files.Add(new FileInfo(path));
                }
            }

            var cutoff = _time.GetUtcNow() - TimeSpan.FromDays(options.RetentionDays);
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(file);
                }
            }

            var kept = files.Where(f => f.Exists).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = kept.Sum(f => f.Length);
            var max = (long)(options.MaxTotalGb * 1024 * 1024 * 1024);

            foreach (var file in kept)
            {
                if (total <= max)
                {
                    break;
                }

                var length = file.Length;
                if (TryDelete(file))
                {
                    total -= length;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            File.Delete(file.FullName);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-")]
    private static partial Regex LogFileNameRegex();
}
