using Microsoft.Extensions.Configuration;

namespace AiCtrl.Core.Config;

public static class AcYamlConfigurationExtensions
{
    public static IConfigurationBuilder AddAcYamlFile(this IConfigurationBuilder builder, string filePath, bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        builder.Add(new AcYamlConfigurationSource
        {
            FilePath = filePath,
            Optional = optional,
        });

        return builder;
    }
}
