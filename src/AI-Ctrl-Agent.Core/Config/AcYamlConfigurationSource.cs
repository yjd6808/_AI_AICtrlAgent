using Microsoft.Extensions.Configuration;

namespace AiCtrl.Core.Config;

public sealed class AcYamlConfigurationSource : IConfigurationSource
{
    public required string FilePath { get; init; }

    public bool Optional { get; init; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new AcYamlConfigurationProvider(FilePath, Optional);
}
