using AiCtrl.Core.Config;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcConfigValidatorTests
{
    private static readonly string[] KnownKeys =
    [
        "configVersion",
        "kestrel:endpoints:*:url",
        "auth:allowedEmails:*",
        "workspaces:*:name",
        "workspaces:*:root",
        "logging:fileLevel",
        "monitor:stallMinutes",
    ];

    private static AcConfigValidationResult Validate(
        Dictionary<string, string?> data,
        Dictionary<string, int>? keyLines = null)
    {
        var validator = new AcConfigValidator(KnownKeys);
        return validator.Validate(data, keyLines ?? [], "test.yaml");
    }

    [Fact]
    public void Missing_config_version_is_error()
    {
        var result = Validate(new Dictionary<string, string?> { ["logging:fileLevel"] = "Debug" });

        result.HasErrors.ShouldBeTrue();
        var issue = result.Issues.ShouldHaveSingleItem();
        issue.Severity.ShouldBe(AcConfigIssueSeverity.Error);
        issue.Key.ShouldBe("configVersion");
        issue.Message.ShouldContain("누락");
    }

    [Fact]
    public void Future_config_version_is_error()
    {
        var result = Validate(new Dictionary<string, string?> { ["configVersion"] = "2" });

        result.HasErrors.ShouldBeTrue();
        result.Issues.Single(i => i.Key == "configVersion").Severity.ShouldBe(AcConfigIssueSeverity.Error);
    }

    [Fact]
    public void Lower_config_version_warns_as_migration()
    {
        var result = Validate(new Dictionary<string, string?> { ["configVersion"] = "0" });

        result.HasErrors.ShouldBeFalse();
        result.Issues.ShouldHaveSingleItem().Severity.ShouldBe(AcConfigIssueSeverity.Warning);
    }

    [Fact]
    public void Non_integer_config_version_is_error()
    {
        var result = Validate(new Dictionary<string, string?> { ["configVersion"] = "1.0" });

        result.HasErrors.ShouldBeTrue();
        result.Issues.Single().Message.ShouldContain("정수");
    }

    [Fact]
    public void Known_keys_including_wildcards_produce_no_issues()
    {
        var data = new Dictionary<string, string?>
        {
            ["configVersion"] = "1",
            ["kestrel:endpoints:local:url"] = "http://127.0.0.1:5080",
            ["auth:allowedEmails:0"] = "a@example.com",
            ["workspaces:0:name"] = "alpha",
            ["workspaces:1:root"] = @"D:\Work\Beta",
            ["logging:fileLevel"] = "Debug",
        };

        var result = Validate(data);

        result.Issues.ShouldBeEmpty();
        result.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_key_warns_with_line_and_format_matches_spec()
    {
        var data = new Dictionary<string, string?>
        {
            ["configVersion"] = "1",
            ["monitr:stallMinutes"] = "5",
        };
        var keyLines = new Dictionary<string, int> { ["monitr:stallMinutes"] = 12 };

        var result = Validate(data, keyLines);

        result.HasErrors.ShouldBeFalse();
        var issue = result.Issues.ShouldHaveSingleItem();
        issue.Severity.ShouldBe(AcConfigIssueSeverity.Warning);
        issue.Key.ShouldBe("monitr:stallMinutes");
        issue.Line.ShouldBe(12);
        issue.Format().ShouldBe("unknown key 'monitr:stallMinutes' (line 12)");
    }

    [Fact]
    public void Wildcard_does_not_match_multiple_segments()
    {
        var data = new Dictionary<string, string?>
        {
            ["configVersion"] = "1",
            ["workspaces:0:root:extra"] = "x",
        };

        var result = Validate(data);

        result.HasErrors.ShouldBeFalse();
        result.Issues.ShouldHaveSingleItem().Key.ShouldBe("workspaces:0:root:extra");
    }
}
