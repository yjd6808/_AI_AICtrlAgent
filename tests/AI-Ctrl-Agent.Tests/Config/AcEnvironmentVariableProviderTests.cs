using AiCtrl.Core.Config;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcEnvironmentVariableProviderTests
{
    [Fact]
    public void Load_maps_prefixed_variables_with_double_underscore_separator()
    {
        const string prefix = "AICTRL_TEST_A_";
        Environment.SetEnvironmentVariable(prefix + "__QUERIES__DEFAULT_TIMEOUT_MS", "1234");
        Environment.SetEnvironmentVariable(prefix + "__SINGLE_KEY", "plain");
        Environment.SetEnvironmentVariable("AICTRL_OTHER_TEST_A___QUERIES__X", "ignored");

        try
        {
            var provider = new AcEnvironmentVariableProvider(prefix + "__");
            provider.Load();

            provider.TryGet("queries:default_timeout_ms", out var value).ShouldBeTrue();
            value.ShouldBe("1234");
            provider.TryGet("single_key", out var plain).ShouldBeTrue();
            plain.ShouldBe("plain");
            provider.TryGet("queries:x", out _).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "__QUERIES__DEFAULT_TIMEOUT_MS", null);
            Environment.SetEnvironmentVariable(prefix + "__SINGLE_KEY", null);
            Environment.SetEnvironmentVariable("AICTRL_OTHER_TEST_A___QUERIES__X", null);
        }
    }

    [Fact]
    public void Builder_layering_lets_environment_override_yaml()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aictrl-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var yamlPath = Path.Combine(directory, "test.yaml");
        File.WriteAllText(yamlPath, "logging:\n  fileLevel: Debug\n");

        try
        {
            const string prefix = "AICTRL_TEST_B_";
            Environment.SetEnvironmentVariable(prefix + "__LOGGING__FILELEVEL", "Trace");

            try
            {
                var configuration = new ConfigurationBuilder()
                    .AddAcYamlFile(yamlPath)
                    .AddAcEnvironmentVariables(prefix + "__")
                    .Build();

                configuration["logging:fileLevel"].ShouldBe("Trace");
            }
            finally
            {
                Environment.SetEnvironmentVariable(prefix + "__LOGGING__FILELEVEL", null);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
