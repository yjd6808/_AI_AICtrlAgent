using Microsoft.Extensions.FileProviders;

namespace AiCtrl.Server;

public static class WebApp
{
    public const string DistPathKey = "web:distPath";

    public static string? ResolveDistPath(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(contentRoot);

        var configured = configuration[DistPathKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRoot, configured);
            return IsDist(path) ? Path.GetFullPath(path) : null;
        }

        string[] candidates =
        [
            Path.Combine(contentRoot, "web", "ai-ctrl-web", "dist"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        ];
        var found = candidates.FirstOrDefault(IsDist);
        return found is null ? null : Path.GetFullPath(found);
    }

    public static void UseAcWebApp(this WebApplication app, string? distPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (distPath is null)
        {
            // Without a built SPA the root still needs a probe response. When dist exists this
            // mapping must not be registered, otherwise it shadows the SPA fallback and the
            // client-side router never runs.
            app.MapGet("/", () => "AI-Ctrl-Agent Server");
            return;
        }

        var provider = new PhysicalFileProvider(distPath);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/hubs"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(Path.Combine(distPath, "index.html"));
        });
    }

    private static bool IsDist(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "index.html"));
}
