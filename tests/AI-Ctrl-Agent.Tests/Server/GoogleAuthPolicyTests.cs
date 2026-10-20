using AiCtrl.Server.Security;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class GoogleAuthPolicyTests
{
    private static readonly string[] Allowed = ["owner@example.com"];

    [Fact]
    public void Verified_allowed_email_passes() =>
        GoogleAuthPolicy.IsAllowed("Owner@Example.com", emailVerified: true, Allowed).ShouldBeTrue();

    [Fact]
    public void Unverified_email_fails() =>
        GoogleAuthPolicy.IsAllowed("owner@example.com", emailVerified: false, Allowed).ShouldBeFalse();

    [Fact]
    public void Empty_allow_list_fails() =>
        GoogleAuthPolicy.IsAllowed("owner@example.com", emailVerified: true, Array.Empty<string>()).ShouldBeFalse();

    [Fact]
    public void Different_email_fails() =>
        GoogleAuthPolicy.IsAllowed("other@example.com", emailVerified: true, Allowed).ShouldBeFalse();
}
