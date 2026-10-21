using Microsoft.Extensions.Configuration;

namespace AiCtrl.Server.Streams;

public sealed class StreamOptions
{
    public int RingChunks { get; set; } = 2000;

    public int RingBytes { get; set; } = 1048576;

    public int IdleDropMinutes { get; set; } = 10;

    public int LongPollMaxMs { get; set; } = 25000;

    public int LeaseSeconds { get; set; } = 60;

    public int EffectiveRingChunks => Math.Max(1, RingChunks);

    public int EffectiveRingBytes => Math.Max(1024, RingBytes);

    public TimeSpan IdleDrop => TimeSpan.FromMinutes(Math.Max(1, IdleDropMinutes));

    public TimeSpan LongPollMax => TimeSpan.FromMilliseconds(Math.Clamp(LongPollMaxMs, 0, 25000));

    public int EffectiveLeaseSeconds => Math.Clamp(LeaseSeconds, 1, 300);

    public static StreamOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("streams");
        return new StreamOptions
        {
            RingChunks = section.GetValue("ringChunks", 2000),
            RingBytes = section.GetValue("ringBytes", 1048576),
            IdleDropMinutes = section.GetValue("idleDropMinutes", 10),
            LongPollMaxMs = section.GetValue("longPollMaxMs", 25000),
            LeaseSeconds = section.GetValue("leaseSeconds", 60),
        };
    }
}
