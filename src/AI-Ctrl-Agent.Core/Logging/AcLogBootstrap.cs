using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AiCtrl.Core.Logging;

public static class AcLogBootstrap
{
    public static AcLogOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AcLogOptions();
        var section = configuration.GetSection("logging");

        options.FileLevel = ParseLevel(section["fileLevel"], options.FileLevel);
        options.ConsoleLevel = ParseLevel(section["consoleLevel"], options.ConsoleLevel);

        if (int.TryParse(section["retentionDays"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays))
        {
            options.RetentionDays = retentionDays;
        }

        if (double.TryParse(section["maxTotalGB"], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxTotalGb))
        {
            options.MaxTotalGb = maxTotalGb;
        }

        return options;
    }

    public static void AddAcLog(this ILoggingBuilder logging, AcLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(options);

        logging.AddProvider(AcLog.Provider);
        logging.AddFilter<AcFileLoggerProvider>(null, options.FileLevel);
        logging.AddFilter<ConsoleLoggerProvider>(null, options.ConsoleLevel);
    }

    private static LogLevel ParseLevel(string? raw, LogLevel fallback) =>
        Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var level) ? level : fallback;
}
