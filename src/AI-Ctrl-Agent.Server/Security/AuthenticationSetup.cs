using System.Security.Claims;
using AiCtrl.Core.Api;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AiCtrl.Server.Security;

public static class AuthenticationSetup
{
    private const string GoogleSecretName = "google-client-secret";

    public static IServiceCollection AddAcAuthentication(
        this IServiceCollection services,
        AuthOptions options,
        IServerSecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        var authentication = services.AddAuthentication(o =>
        {
            o.DefaultScheme = GoogleAuthPolicy.CookieScheme;
        });

        authentication.AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(
            AgentTokenDefaults.Scheme,
            _ => { });

        authentication.AddCookie(GoogleAuthPolicy.CookieScheme, o =>
        {
            o.Cookie.Name = "__Host-aictrl";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            o.LoginPath = "/api/v1/auth/login";
        });

        var clientSecret = secrets.Get(GoogleSecretName);
        if (!string.IsNullOrWhiteSpace(options.GoogleClientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            authentication.AddGoogle(GoogleDefaults.AuthenticationScheme, o =>
            {
                o.ClientId = options.GoogleClientId;
                o.ClientSecret = clientSecret;
                o.CallbackPath = "/signin-google";
                o.SignInScheme = GoogleAuthPolicy.CookieScheme;
                o.Scope.Add("email");
                o.Scope.Add("profile");
                o.Events.OnRemoteFailure = async context =>
                {
                    context.HandleResponse();
                    await Results.Json(
                        new AcProblemDetails(
                            "about:blank",
                            "Forbidden",
                            StatusCodes.Status403Forbidden,
                            "허용되지 않은 Google 계정입니다.",
                            context.Request.Path,
                            AcErrorCodes.EmailNotAllowed),
                        AcJsonContext.Default.AcProblemDetails,
                        statusCode: StatusCodes.Status403Forbidden,
                        contentType: "application/problem+json").ExecuteAsync(context.HttpContext).ConfigureAwait(false);
                };
                o.Events.OnCreatingTicket = async context =>
                {
                    var email = context.Identity?.FindFirst(ClaimTypes.Email)?.Value;
                    var verified = string.Equals(
                        context.Identity?.FindFirst("email_verified")?.Value,
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                    if (!GoogleAuthPolicy.IsAllowed(email, verified, options.AllowedEmails))
                    {
                        context.Fail("email is not allowed");
                        return;
                    }

                    var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                    context.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, email!));
                    context.Identity.AddClaim(new Claim("auth_time", time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

                    var db = context.HttpContext.RequestServices.GetRequiredService<ServerDb>();
                    await db.GetOrCreateUserAsync(email!, context.Identity.FindFirst(ClaimTypes.Name)?.Value, context.HttpContext.RequestAborted)
                        .ConfigureAwait(false);
                };
            });
        }

        services.AddAuthorization(o =>
        {
            o.AddPolicy(GoogleAuthPolicy.BrowserPolicy, policy => policy
                .AddAuthenticationSchemes(GoogleAuthPolicy.CookieScheme)
                .RequireAuthenticatedUser());
            o.AddPolicy(AgentTokenDefaults.Policy, policy => policy
                .AddAuthenticationSchemes(AgentTokenDefaults.Scheme)
                .RequireAuthenticatedUser());
        });

        return services;
    }
}
