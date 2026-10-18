using Microsoft.Extensions.Configuration;

namespace AiCtrl.Core.Config;

public static class AcEnvironmentVariableExtensions
{
    public const string DefaultPrefix = "AICTRL__";

    public static IConfigurationBuilder AddAcEnvironmentVariables(
        this IConfigurationBuilder builder,
        string prefix = DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        builder.Add(new AcEnvironmentVariableSource { Prefix = prefix });
        return builder;
    }
}
