using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Logging;

public static class AcLog
{
    private static readonly object Gate = new();
    private static AcLogService? _service;
    private static AcFileLoggerProvider? _provider;

    public static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _service is not null;
            }
        }
    }

    public static ILoggerProvider Provider
    {
        get
        {
            lock (Gate)
            {
                return _provider ?? throw new InvalidOperationException("AcLog.Initialize must be called first.");
            }
        }
    }

    public static void Initialize(string appName, AcLogOptions? options = null)
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                return;
            }

            options ??= new AcLogOptions();
            var envDirectory = Environment.GetEnvironmentVariable("AICTRL_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(envDirectory))
            {
                options.Directory = envDirectory;
            }

            var service = new AcLogService(appName, options);
            _service = service;
            _provider = new AcFileLoggerProvider(service);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                service.CrashWrite(LogLevel.Critical, "App", "Unhandled exception", e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, e) =>
                service.CrashWrite(LogLevel.Error, "App", "Unobserved task exception", e.Exception);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => service.CrashShutdown(Environment.ExitCode);

            Console.CancelKeyPress += (_, _) =>
                service.CrashWrite(LogLevel.Warning, "App", "Console cancel requested");
        }
    }

    public static void Write(LogLevel level, string category, string message) =>
        _service?.Write(level, category, message);

    public static void AppendConfigSummary(string summary) => _service?.AppendConfigSummary(summary);

    public static void Shutdown(int exitCode = 0) => _service?.Shutdown(exitCode);

    public static string? FilePath => _service?.FilePath;
}
