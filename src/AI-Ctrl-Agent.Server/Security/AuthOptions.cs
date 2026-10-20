using Microsoft.Extensions.Configuration;

namespace AiCtrl.Server.Security;

public sealed record AuthOptions
{
    public string GoogleClientId { get; init; } = string.Empty;

    public IReadOnlyList<string> AllowedEmails { get; init; } = Array.Empty<string>();

    public int StepUpMinutes { get; init; } = 30;

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("auth");
        var emails = section.GetSection("allowedEmails").GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant())
            .ToArray();
        var stepUp = int.TryParse(section["stepUpMinutes"], out var minutes) ? minutes : 30;
        return new AuthOptions
        {
            GoogleClientId = section["googleClientId"] ?? string.Empty,
            AllowedEmails = emails,
            StepUpMinutes = stepUp,
        };
    }
}

public static class GoogleAuthPolicy
{
    public const string CookieScheme = "Cookies";

    public const string BrowserPolicy = "Browser";

    public static bool IsAllowed(string? email, bool emailVerified, IReadOnlyList<string> allowedEmails)
    {
        if (!emailVerified || string.IsNullOrWhiteSpace(email) || allowedEmails.Count == 0)
        {
            return false;
        }

        return allowedEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
