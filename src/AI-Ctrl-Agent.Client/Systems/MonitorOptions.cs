using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Systems;

public sealed record MonitorOptions
{
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan StallCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    public bool LogReasoning { get; init; } = true;

    public int LogToolInputMaxKb { get; init; } = 2;

    public int LogToolOutputMaxKb { get; init; } = 64;

    public static MonitorOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("monitor");
        return new MonitorOptions
        {
            StatusPollInterval = TimeSpan.FromSeconds(ReadDouble(section, "statusPollSeconds", 5)),
            StallThreshold = TimeSpan.FromMinutes(ReadDouble(section, "stallMinutes", 10)),
            LogReasoning = ReadBool(section, "logReasoning", true),
            LogToolOutputMaxKb = (int)ReadDouble(section, "logToolOutputMaxKB", 64),
        };
    }

    private static double ReadDouble(IConfiguration section, string key, double fallback) =>
        double.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static bool ReadBool(IConfiguration section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;
}
