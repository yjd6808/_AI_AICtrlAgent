using System.Security.Claims;
using System.Text.Encodings.Web;
using AiCtrl.Core.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AiCtrl.Server.Security;

public sealed class AgentTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AgentTokenService _tokens;

    public AgentTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AgentTokenService tokens)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var validation = _tokens.Validate(token);
        if (!validation.Valid || string.IsNullOrEmpty(validation.MachineId))
        {
            return Task.FromResult(AuthenticateResult.Fail("invalid agent token"));
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, validation.MachineId),
                new Claim(AgentTokenDefaults.MachineIdClaim, validation.MachineId),
            },
            AgentTokenDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AgentTokenDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var query = Request.Query["access_token"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }
}
