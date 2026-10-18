using System.Text;
using AiCtrl.Core.Config;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcOverridesWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aictrl-overrides-" + Guid.NewGuid().ToString("N"));

    public AcOverridesWriterTests()
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

    private string OverridesPath => Path.Combine(_directory, "client.overrides.yaml");

    [Fact]
    public void Write_serializes_nested_maps_and_sequences_in_flat_key_order()
    {
        var writer = new AcOverridesWriter(OverridesPath);

        var hash = writer.Write(new Dictionary<string, string?>
        {
            ["registeredWorkspaces:0:name"] = "alpha",
            ["registeredWorkspaces:0:root"] = @"D:\Work\Alpha",
            ["registeredWorkspaces:1:name"] = "beta",
            ["registeredWorkspaces:1:root"] = @"D:\Work\Beta",
            ["power:keepAwakeDefault"] = "false",
        });

        var text = File.ReadAllText(OverridesPath, Encoding.UTF8);
        text.ShouldStartWith("configVersion: 1\n");
        text.ShouldContain("registeredWorkspaces:\n  - name: alpha\n    root: 'D:\\Work\\Alpha'\n  - name: beta\n    root: 'D:\\Work\\Beta'\n");
        text.ShouldContain("power:\n  keepAwakeDefault: false\n");
        hash.ShouldBe(AcYamlConfigurationProvider.ComputeHash(text));

        var reloaded = AcYamlFlattener.Flatten(text, OverridesPath);
        reloaded.Values["registeredWorkspaces:1:root"].ShouldBe(@"D:\Work\Beta");
        reloaded.Values["power:keepAwakeDefault"].ShouldBe("false");
    }

    [Fact]
    public void Write_round_trips_values_through_flattener()
    {
        var writer = new AcOverridesWriter(OverridesPath);
        var updates = new Dictionary<string, string?>
        {
            ["registeredWorkspaces:0:name"] = "alpha",
            ["registeredWorkspaces:0:root"] = @"D:\Work\Alpha",
            ["power:keepAwakeDefault"] = "true",
        };

        writer.Write(updates);

        var reloaded = AcYamlFlattener.Flatten(File.ReadAllText(OverridesPath, Encoding.UTF8), OverridesPath);
        foreach (var pair in updates)
        {
            reloaded.Values[pair.Key].ShouldBe(pair.Value);
        }

        reloaded.Values["configVersion"].ShouldBe("1");
    }

    [Fact]
    public void Write_removes_requested_keys_and_keeps_existing_values()
    {
        var writer = new AcOverridesWriter(OverridesPath);
        writer.Write(new Dictionary<string, string?>
        {
            ["power:keepAwakeDefault"] = "true",
            ["registeredWorkspaces:0:name"] = "alpha",
        });

        writer.Write(
            new Dictionary<string, string?> { ["power:keepAwakeDefault"] = "false" },
            removedKeys: ["registeredWorkspaces:0:name"]);

        var reloaded = AcYamlFlattener.Flatten(File.ReadAllText(OverridesPath, Encoding.UTF8), OverridesPath);
        reloaded.Values["power:keepAwakeDefault"].ShouldBe("false");
        reloaded.Values.ContainsKey("registeredWorkspaces:0:name").ShouldBeFalse();
    }

    [Fact]
    public void Second_write_creates_backup_and_leaves_no_temp_file()
    {
        var writer = new AcOverridesWriter(OverridesPath);
        writer.Write(new Dictionary<string, string?> { ["power:keepAwakeDefault"] = "true" });
        var firstContent = File.ReadAllText(OverridesPath, Encoding.UTF8);

        writer.Write(new Dictionary<string, string?> { ["power:keepAwakeDefault"] = "false" });

        File.Exists(OverridesPath + ".tmp").ShouldBeFalse();
        File.ReadAllText(OverridesPath + ".bak", Encoding.UTF8).ShouldBe(firstContent);
        File.ReadAllText(OverridesPath, Encoding.UTF8).ShouldContain("keepAwakeDefault: false");
    }

    [Fact]
    public void Empty_updates_still_writes_config_version_only()
    {
        var writer = new AcOverridesWriter(OverridesPath);

        writer.Write(new Dictionary<string, string?>());

        File.ReadAllText(OverridesPath, Encoding.UTF8).ShouldBe("configVersion: 1\n");
    }

    [Fact]
    public void Values_with_colons_and_quotes_are_single_quoted_safely()
    {
        var writer = new AcOverridesWriter(OverridesPath);

        writer.Write(new Dictionary<string, string?>
        {
            ["note:text"] = "it's: tricky",
        });

        var text = File.ReadAllText(OverridesPath, Encoding.UTF8);
        text.ShouldContain("text: 'it''s: tricky'\n");

        var reloaded = AcYamlFlattener.Flatten(text, OverridesPath);
        reloaded.Values["note:text"].ShouldBe("it's: tricky");
    }
}
