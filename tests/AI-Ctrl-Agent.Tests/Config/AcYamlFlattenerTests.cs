using AiCtrl.Core.Config;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcYamlFlattenerTests
{
    [Fact]
    public void Flattens_nested_mapping_and_sequences()
    {
        const string yaml = """
            kestrel:
              endpoints:
                local:
                  url: 'http://127.0.0.1:5080'
            auth:
              allowedEmails:
                - 'a@example.com'
                - 'b@example.com'
            workspaces:
              - name: alpha
                root: 'C:\Work\Alpha'
            """;

        var document = AcYamlFlattener.Flatten(yaml, "test.yaml");

        document.Values["kestrel:endpoints:local:url"].ShouldBe("http://127.0.0.1:5080");
        document.Values["auth:allowedEmails:0"].ShouldBe("a@example.com");
        document.Values["auth:allowedEmails:1"].ShouldBe("b@example.com");
        document.Values["workspaces:0:name"].ShouldBe("alpha");
        document.Values["workspaces:0:root"].ShouldBe(@"C:\Work\Alpha");
        document.Values.Count.ShouldBe(5);
    }

    [Fact]
    public void Keeps_scalars_as_strings_without_implicit_conversion()
    {
        const string yaml = """
            flag: no
            other: yes
            mode: 0755
            enabled: true
            ratio: 1.5
            """;

        var document = AcYamlFlattener.Flatten(yaml, "test.yaml");

        document.Values["flag"].ShouldBe("no");
        document.Values["other"].ShouldBe("yes");
        document.Values["mode"].ShouldBe("0755");
        document.Values["enabled"].ShouldBe("true");
        document.Values["ratio"].ShouldBe("1.5");
    }

    [Fact]
    public void Single_quoted_path_keeps_backslashes()
    {
        const string yaml = "path: 'D:\\AiCtrl\\Server\\config'\n";

        var document = AcYamlFlattener.Flatten(yaml, "test.yaml");

        document.Values["path"].ShouldBe(@"D:\AiCtrl\Server\config");
    }

    [Fact]
    public void Records_key_line_numbers()
    {
        const string yaml = "a:\n  b: 1\n";

        var document = AcYamlFlattener.Flatten(yaml, "test.yaml");

        document.KeyLines["a"].ShouldBe(1);
        document.KeyLines["a:b"].ShouldBe(2);
    }

    [Fact]
    public void Empty_value_becomes_empty_string()
    {
        const string yaml = "a:\n";

        var document = AcYamlFlattener.Flatten(yaml, "test.yaml");

        document.Values["a"].ShouldBe(string.Empty);
    }

    [Fact]
    public void Empty_and_comment_only_documents_produce_no_values()
    {
        AcYamlFlattener.Flatten(string.Empty, "test.yaml").Values.ShouldBeEmpty();
        AcYamlFlattener.Flatten("# comment only\n", "test.yaml").Values.ShouldBeEmpty();
    }

    [Fact]
    public void Duplicate_key_fails_with_line_number()
    {
        const string yaml = "a: 1\nb: 2\na: 3\n";

        var exception = Should.Throw<AcConfigException>(() => AcYamlFlattener.Flatten(yaml, "test.yaml"));

        exception.Line.ShouldBe(3);
        exception.Reason.ShouldContain("중복 키");
    }

    [Fact]
    public void Tab_indentation_fails_with_line_number()
    {
        const string yaml = "a:\n\tb: 1\n";

        var exception = Should.Throw<AcConfigException>(() => AcYamlFlattener.Flatten(yaml, "test.yaml"));

        exception.Line.ShouldBe(2);
        exception.Reason.ShouldContain("탭");
    }

    [Fact]
    public void Anchor_fails()
    {
        const string yaml = "a: &shared 1\n";

        var exception = Should.Throw<AcConfigException>(() => AcYamlFlattener.Flatten(yaml, "test.yaml"));

        exception.Reason.ShouldContain("앵커");
    }

    [Fact]
    public void Unresolved_alias_fails()
    {
        const string yaml = "a: *missing\n";

        var exception = Should.Throw<AcConfigException>(() => AcYamlFlattener.Flatten(yaml, "test.yaml"));

        exception.Line.ShouldBe(1);
    }

    [Fact]
    public void Multi_document_fails()
    {
        const string yaml = "a: 1\n---\nb: 2\n";

        var exception = Should.Throw<AcConfigException>(() => AcYamlFlattener.Flatten(yaml, "test.yaml"));

        exception.Reason.ShouldContain("다중 문서");
    }

    [Fact]
    public void Root_scalar_fails()
    {
        const string yaml = "just text\n";

        var exception = Should.Throw<AcConfigException>(() => AcYamlFlattener.Flatten(yaml, "test.yaml"));

        exception.Reason.ShouldContain("최상위");
    }
}
