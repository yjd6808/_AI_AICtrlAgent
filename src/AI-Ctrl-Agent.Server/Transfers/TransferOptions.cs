using Microsoft.Extensions.Configuration;

namespace AiCtrl.Server.Transfers;

public sealed record TransferOptions
{
    public string Dir { get; init; } = @"C:\AiCtrl\AgentServer\data\transfers";

    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long MaxTotalBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    public int ChunkBytes { get; init; } = 8 * 1024 * 1024;

    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    public static TransferOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("transfers");
        return new TransferOptions
        {
            Dir = ReadString(section, "dir", @"C:\AiCtrl\AgentServer\data\transfers"),
            MaxFileBytes = ReadLong(section, "maxFileBytes", 2L * 1024 * 1024 * 1024),
            MaxTotalBytes = ReadLong(section, "maxTotalBytes", 20L * 1024 * 1024 * 1024),
            ChunkBytes = (int)ReadLong(section, "chunkBytes", 8 * 1024 * 1024),
            Ttl = TimeSpan.FromHours(ReadLong(section, "ttlHours", 24)),
        };
    }

    private static string ReadString(IConfigurationSection section, string key, string fallback)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static long ReadLong(IConfigurationSection section, string key, long fallback) =>
        long.TryParse(section[key], out var value) && value > 0 ? value : fallback;
}
