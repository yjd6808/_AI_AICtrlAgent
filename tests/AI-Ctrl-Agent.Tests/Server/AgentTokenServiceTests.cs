using AiCtrl.Server.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class AgentTokenServiceTests
{
    [Fact]
    public void Token_round_trips_machine_id_and_expires()
    {
        var time = new FakeTimeProvider();
        var service = new AgentTokenService(new InMemoryServerSecretStore(), time);

        var token = service.Create("m_abc", out var expiresAt);
        var result = service.Validate(token);

        result.Valid.ShouldBeTrue();
        result.MachineId.ShouldBe("m_abc");
        result.ExpiresAt.ShouldBe(expiresAt);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var time = new FakeTimeProvider();
        var service = new AgentTokenService(new InMemoryServerSecretStore(), time);
        var token = service.Create("m_abc", out _);

        time.Advance(TimeSpan.FromSeconds(AiCtrl.Core.Protocol.AgentTokenDefaults.LifetimeSeconds + 1));

        service.Validate(token).Valid.ShouldBeFalse();
    }

    [Fact]
    public void Tampered_signature_is_rejected()
    {
        var service = new AgentTokenService(new InMemoryServerSecretStore(), TimeProvider.System);
        var token = service.Create("m_abc", out _);
        var parts = token.Split('.');
        var tampered = parts[0] + "." + parts[1] + "." + (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];

        service.Validate(tampered).Valid.ShouldBeFalse();
    }

    [Fact]
    public void Malformed_token_is_rejected()
    {
        var service = new AgentTokenService(new InMemoryServerSecretStore(), TimeProvider.System);

        service.Validate("not-a-token").Valid.ShouldBeFalse();
        service.Validate("a.b.c").Valid.ShouldBeFalse();
        service.Validate(string.Empty).Valid.ShouldBeFalse();
    }
}
