using System.Globalization;
using AiCtrl.Client.Storage;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Systems;

public sealed record LocalApiOptions
{
    public int Port { get; init; } = 47800;

    public string TokenPath { get; init; } = ClientPaths.LocalTokenPath;

    public static LocalApiOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("localApi");
        var port = int.TryParse(section["port"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= 65535
                ? value
                : 47800;
        var tokenPath = section["tokenPath"];

        return new LocalApiOptions
        {
            Port = port,
            TokenPath = string.IsNullOrWhiteSpace(tokenPath) ? ClientPaths.LocalTokenPath : tokenPath,
        };
    }
}
