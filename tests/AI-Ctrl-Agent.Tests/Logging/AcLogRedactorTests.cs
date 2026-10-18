using AiCtrl.Core.Logging;
using Shouldly;

namespace AiCtrl.Tests.Logging;

public sealed class AcLogRedactorTests
{
    [Fact]
    public void Redacts_authorization_header() =>
        AcLogRedactor.Redact("Authorization: Bearer abc123").ShouldBe("Authorization: ****");

    [Fact]
    public void Redacts_bearer_token() =>
        AcLogRedactor.Redact("failed call Bearer abc123.def-ghi/jkl ok").ShouldBe("failed call Bearer **** ok");

    [Fact]
    public void Redacts_pat() =>
        AcLogRedactor.Redact("token=act_ABCdef0123 rest").ShouldBe("token=act_**** rest");

    [Fact]
    public void Redacts_password_assignment() =>
        AcLogRedactor.Redact("password=hunter2 user=bob").ShouldBe("password=**** user=bob");

    [Fact]
    public void Redacts_opencode_server_password() =>
        AcLogRedactor.Redact("OPENCODE_SERVER_PASSWORD=s3cret!").ShouldBe("OPENCODE_SERVER_PASSWORD=****");

    [Fact]
    public void Redacts_jwt() =>
        AcLogRedactor.Redact("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc-123_xyz").ShouldBe("jwt ****");

    [Fact]
    public void Redacts_enroll_code() =>
        AcLogRedactor.Redact("enroll code=ABCD-1234 done").ShouldBe("enroll code=**** done");

    [Fact]
    public void Redacts_cli_code_argument() =>
        AcLogRedactor.Redact("enroll --server https://ctrl.example.com --code AB12-CD34")
            .ShouldBe("enroll --server https://ctrl.example.com --code ****");

    [Fact]
    public void Redacts_multiple_secrets_in_one_line() =>
        AcLogRedactor.Redact("Authorization: Bearer aaa act_bbb password=ccc")
            .ShouldBe("Authorization: **** act_**** password=****");

    [Fact]
    public void Leaves_plain_text_unchanged() =>
        AcLogRedactor.Redact("Connected to server in 412ms").ShouldBe("Connected to server in 412ms");

    [Fact]
    public void Returns_empty_for_null() => AcLogRedactor.Redact(null).ShouldBeEmpty();
}
