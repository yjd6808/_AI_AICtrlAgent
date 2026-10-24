using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed record OpenCodeProbeResult(string? Version, OpenCodeRoutes Routes, CapabilitySet Capabilities, bool Reachable);

public sealed partial class OpenCodeCapabilityProbe
{
    private readonly OpenCodeHttpApi _api;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenCodeProbeResult? _cached;

    public OpenCodeCapabilityProbe(OpenCodeHttpApi api)
    {
        _api = api;
    }

    public async ValueTask<OpenCodeProbeResult> ProbeAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _cached is { } cachedAgain)
            {
                return cachedAgain;
            }

            var result = await ProbeCoreAsync(ct).ConfigureAwait(false);
            _cached = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<OpenCodeProbeResult> ProbeCoreAsync(CancellationToken ct)
    {
        var (healthReachable, version, versionRoutes) = await ReadVersionAsync(ct).ConfigureAwait(false);
        var (docReachable, paths, docRoutes) = await ReadDocPathsAsync(ct).ConfigureAwait(false);
        var routes = SelectRoutes(paths, docRoutes, versionRoutes);
        var capabilities = paths is null ? CapabilitySet.None : ComputeCapabilities(paths);
        return new OpenCodeProbeResult(version, routes, capabilities, healthReachable || docReachable);
    }

    private async ValueTask<(bool Reachable, string? Version, OpenCodeRoutes? Routes)> ReadVersionAsync(CancellationToken ct)
    {
        var reachable = false;
        string? version = null;
        OpenCodeRoutes? versionRoutes = null;
        foreach (var routes in new[] { OpenCodeRoutes.V1, OpenCodeRoutes.V2 })
        {
            var (responded, body) = await TryGetAsync(routes.Health, ct).ConfigureAwait(false);
            reachable |= responded;
            if (version is null &&
                body is { ValueKind: JsonValueKind.Object } health &&
                health.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString();
                versionRoutes = routes;
            }
        }

        return (reachable, version, versionRoutes);
    }

    private async ValueTask<(bool Reachable, HashSet<string>? Paths, OpenCodeRoutes? Routes)> ReadDocPathsAsync(CancellationToken ct)
    {
        var reachable = false;
        foreach (var routes in new[] { OpenCodeRoutes.V1, OpenCodeRoutes.V2 })
        {
            var (responded, body) = await TryGetAsync(routes.Doc, ct).ConfigureAwait(false);
            reachable |= responded;
            if (body is not { ValueKind: JsonValueKind.Object } doc ||
                !doc.TryGetProperty("paths", out var paths) ||
                paths.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var normalized = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in paths.EnumerateObject())
            {
                normalized.Add(NormalizePath(property.Name));
            }

            return (true, normalized, routes);
        }

        return (reachable, null, null);
    }

    private static OpenCodeRoutes SelectRoutes(
        HashSet<string>? paths,
        OpenCodeRoutes? docRoutes,
        OpenCodeRoutes? versionRoutes)
    {
        if (paths is not null)
        {
            if (paths.Contains("/api/session") || paths.Contains("/api/info"))
            {
                return OpenCodeRoutes.V2;
            }

            if (paths.Contains("/session") || paths.Contains("/global/health"))
            {
                return OpenCodeRoutes.V1;
            }
        }

        return versionRoutes ?? docRoutes ?? OpenCodeRoutes.V1;
    }

    private static CapabilitySet ComputeCapabilities(HashSet<string> normalized)
    {
        var capabilities = CapabilitySet.None;

        if (Has(normalized, "/session", "/api/session"))
        {
            capabilities |= CapabilitySet.ListSessions;
        }

        if (Has(normalized, "/session/*/message", "/api/session/*/message"))
        {
            capabilities |= CapabilitySet.ReadMessages;
        }

        if (Has(normalized, "/session/*/abort", "/api/session/*/interrupt", "/session/*/interrupt"))
        {
            capabilities |= CapabilitySet.Abort;
        }

        if (Has(normalized, "/session/*/prompt_async", "/api/session/*/prompt", "/session/*/prompt"))
        {
            capabilities |= CapabilitySet.PromptAsync | CapabilitySet.Prompt;
        }

        if (Has(normalized, "/config/providers", "/api/model", "/model"))
        {
            capabilities |= CapabilitySet.ModelSelect;
        }

        if (Has(
                normalized,
                "/session/*/permissions/*",
                "/api/session/*/permission/*/reply",
                "/session/*/permission/*/reply"))
        {
            capabilities |= CapabilitySet.Permissions;
        }

        if (Has(normalized, "/event", "/api/event"))
        {
            capabilities |= CapabilitySet.LiveEvents;
        }

        if (Has(normalized, "/session/*/children", "/api/session/*/children"))
        {
            capabilities |= CapabilitySet.SubSessions;
        }

        return capabilities;
    }

    private static bool Has(HashSet<string> paths, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (paths.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<(bool Responded, JsonElement? Body)> TryGetAsync(string path, CancellationToken ct)
    {
        try
        {
            var (status, body, _) = await _api.TryGetJsonAsync(path, ct).ConfigureAwait(false);
            return (true, status == HttpStatusCode.OK ? body : null);
        }
        catch (HttpRequestException)
        {
            return (false, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    private static string NormalizePath(string path) => PlaceholderRegex().Replace(path, "*");

    [GeneratedRegex("\\{[^}]+\\}")]
    private static partial Regex PlaceholderRegex();
}
