using AiCtrl.Core.Config;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcYamlConfigurationProviderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aictrl-yaml-" + Guid.NewGuid().ToString("N"));

    public AcYamlConfigurationProviderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteYaml(string content)
    {
        var path = Path.Combine(_directory, "test.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_reads_file_and_exposes_key_lines()
    {
        var path = WriteYaml("kestrel:\n  port: 5080\n");
        var provider = new AcYamlConfigurationProvider(path, optional: false);

        provider.Load();

        provider.TryGet("kestrel:port", out var port).ShouldBeTrue();
        port.ShouldBe("5080");
        provider.KeyLines["kestrel:port"].ShouldBe(2);
    }

    [Fact]
    public void Optional_missing_file_yields_empty_data()
    {
        var provider = new AcYamlConfigurationProvider(Path.Combine(_directory, "absent.yaml"), optional: true);

        provider.Load();

        provider.TryGet("kestrel:port", out _).ShouldBeFalse();
    }

    [Fact]
    public void Required_missing_file_throws()
    {
        var provider = new AcYamlConfigurationProvider(Path.Combine(_directory, "absent.yaml"), optional: false);

        var exception = Should.Throw<AcConfigException>(() => provider.Load());

        exception.Reason.ShouldContain("없습니다");
    }

    [Fact]
    public void Invalid_yaml_throws_with_file_and_line()
    {
        var path = WriteYaml("a: [1, 2\n");
        var provider = new AcYamlConfigurationProvider(path, optional: false);

        var exception = Should.Throw<AcConfigException>(() => provider.Load());

        exception.FilePath.ShouldBe(path);
        exception.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Builder_layering_applies_later_sources_on_top()
    {
        var path = WriteYaml("kestrel:\n  port: 5080\n");
        var configuration = new ConfigurationBuilder()
            .AddAcYamlFile(path)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kestrel:port"] = "9090",
                ["extra:key"] = "value",
            })
            .Build();

        configuration["kestrel:port"].ShouldBe("9090");
        configuration["extra:key"].ShouldBe("value");
    }

    [Fact]
    public void Builder_with_optional_missing_file_builds_empty_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddAcYamlFile(Path.Combine(_directory, "absent.yaml"), optional: true)
            .Build();

        configuration.AsEnumerable().ShouldBeEmpty();
    }
}
