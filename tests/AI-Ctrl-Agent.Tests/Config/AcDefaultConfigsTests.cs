using AiCtrl.Core.Config;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcDefaultConfigsTests
{
    [Fact]
    public void EnsureFile_creates_missing_file_and_directories()
    {
        var root = NewTempRoot();
        try
        {
            var path = Path.Combine(root, "config", "server.yaml");

            var created = AcDefaultConfigs.EnsureFile(path, "configVersion: 1\n");

            created.ShouldBeTrue();
            File.Exists(path).ShouldBeTrue();
            File.ReadAllText(path).ShouldBe("configVersion: 1\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureFile_does_not_overwrite_existing_file()
    {
        var root = NewTempRoot();
        try
        {
            var path = Path.Combine(root, "server.yaml");
            File.WriteAllText(path, "configVersion: 1\nserverUrl: 'http://edited'\n");

            var created = AcDefaultConfigs.EnsureFile(path, AcDefaultConfigs.ServerYaml);

            created.ShouldBeFalse();
            File.ReadAllText(path).ShouldContain("http://edited");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OverridesPathFor_derives_overrides_file_next_to_config()
    {
        var path = Path.Combine(@"C:\AiCtrl\AgentServer\config", "server.yaml");

        AcDefaultConfigs.OverridesPathFor(path).ShouldBe(@"C:\AiCtrl\AgentServer\config\server.overrides.yaml");
    }

    [Fact]
    public void Default_server_config_path_uses_AgentServer_home()
    {
        AcDefaultConfigs.DefaultServerConfigPath.ShouldBe(@"C:\AiCtrl\AgentServer\config\server.yaml");
    }

    [Fact]
    public void ServerYaml_flattens_with_expected_defaults()
    {
        var document = AcYamlFlattener.Flatten(AcDefaultConfigs.ServerYaml, "server.yaml");

        document.Values["configVersion"].ShouldBe("1");
        document.Values["kestrel:endpoints:local:url"].ShouldBe("http://127.0.0.1:5080");
        document.Values["requests:waitSeconds:session.abort_and_prompt"].ShouldBe("30");
        document.Values["logging:fileLevel"].ShouldBe("Debug");
        document.Values["geo:allowCountries:0"].ShouldBe("KR");
    }

    [Fact]
    public void ClientYaml_flattens_with_expected_defaults()
    {
        var document = AcYamlFlattener.Flatten(AcDefaultConfigs.ClientYaml, "client.yaml");

        document.Values["configVersion"].ShouldBe("1");
        document.Values["serverUrl"].ShouldBe("http://127.0.0.1:5080");
        document.Values["opencode:mode"].ShouldBe("Managed");
        document.Values["vpn:fallback:readyUrl"].ShouldBe("http://127.0.0.1:20241/ready");
        document.Values["logging:retentionDays"].ShouldBe("30");
    }

    [Fact]
    public void DefaultYaml_passes_config_validation_without_errors()
    {
        var validator = new AcConfigValidator(AcYamlFlattener.Flatten(AcDefaultConfigs.ServerYaml, "s").Values.Keys);
        var document = AcYamlFlattener.Flatten(AcDefaultConfigs.ServerYaml, "server.yaml");

        var result = validator.Validate(document.Values, document.KeyLines, "server.yaml");

        result.Issues.ShouldNotContain(i => i.Severity == AcConfigIssueSeverity.Error);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
