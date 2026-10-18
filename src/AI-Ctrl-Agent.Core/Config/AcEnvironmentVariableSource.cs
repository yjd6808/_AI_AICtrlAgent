using Microsoft.Extensions.Configuration;

namespace AiCtrl.Core.Config;

public sealed class AcEnvironmentVariableSource : IConfigurationSource
{
    public string Prefix { get; init; } = AcEnvironmentVariableExtensions.DefaultPrefix;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new AcEnvironmentVariableProvider(Prefix);
}
