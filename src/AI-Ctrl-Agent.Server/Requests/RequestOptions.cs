using Microsoft.Extensions.Configuration;

namespace AiCtrl.Server.Requests;

public sealed class RequestOptions
{
    public int AckSeconds { get; set; } = 10;

    public int CommandSeconds { get; set; } = 30;

    public int QuerySeconds { get; set; } = 10;

    public int OfflineWaitSeconds { get; set; } = 10;

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan AckTimeout => TimeSpan.FromSeconds(Math.Max(1, AckSeconds));

    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(Math.Max(1, CommandSeconds));

    public TimeSpan QueryTimeout => TimeSpan.FromSeconds(Math.Max(1, QuerySeconds));

    public TimeSpan OfflineWait => TimeSpan.FromSeconds(Math.Max(0, OfflineWaitSeconds));

    public static RequestOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("requests");
        return new RequestOptions
        {
            AckSeconds = section.GetValue("ackSeconds", 10),
            CommandSeconds = section.GetValue("commandSeconds", 30),
            QuerySeconds = section.GetValue("querySeconds", 10),
            OfflineWaitSeconds = section.GetValue("waitSeconds", 10),
            MaxAttempts = section.GetValue("maxAttempts", 3),
        };
    }
}
