using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Link;

public sealed record ServerLinkOptions
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:5080";

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public bool Enabled { get; init; } = true;

    public static ServerLinkOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var url = configuration["serverUrl"];
        var backoff = int.TryParse(configuration["reconnect:maxBackoffSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
                ? seconds
                : 30;
        return new ServerLinkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:5080" : url!,
            MaxBackoff = TimeSpan.FromSeconds(backoff),
        };
    }
}
