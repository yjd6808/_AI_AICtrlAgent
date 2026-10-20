using Microsoft.Extensions.Configuration;

namespace AiCtrl.Server.Persistence;

public sealed record ServerDbOptions
{
    public const string DefaultPath = @"C:\AiCtrl\Server\data\aictrl.db";

    public string Path { get; init; } = DefaultPath;

    public static ServerDbOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration["database:path"];
        return new ServerDbOptions
        {
            Path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path,
        };
    }

    public string ConnectionString
    {
        get
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return $"Data Source={Path}";
        }
    }
}
