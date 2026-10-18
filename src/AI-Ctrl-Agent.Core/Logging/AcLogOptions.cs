using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Logging;

public sealed class AcLogOptions
{
    public LogLevel FileLevel { get; set; } = LogLevel.Debug;

    public LogLevel ConsoleLevel { get; set; } = LogLevel.Information;

    public int RetentionDays { get; set; } = 30;

    public double MaxTotalGb { get; set; } = 5;

    public long MaxFileBytes { get; set; } = 500L * 1024 * 1024;

    public string? Directory { get; set; }
}
