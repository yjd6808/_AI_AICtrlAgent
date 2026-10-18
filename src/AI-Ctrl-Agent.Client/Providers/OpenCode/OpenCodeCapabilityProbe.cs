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
        var (healthReachable, version) = await ReadVersionAsync(ct).ConfigureAwait(false);
        var (docReachable, paths) = await ReadDocPathsAsync(ct).ConfigureAwait(false);
        var routes = SelectRoutes(paths);
        var capabilities = paths is null ? CapabilitySet.None : ComputeCapabilities(paths);
        return new OpenCodeProbeResult(version, routes, capabilities, healthReachable || docReachable);
    }

    private async ValueTask<(bool Reachable, string? Version)> ReadVersionAsync(CancellationToken ct)
    {
        var reachable = false;
        foreach (var routes in new[] { OpenCodeRoutes.V1, OpenCodeRoutes.V2 })
        {
            var (responded, body) = await TryGetAsync(routes.Health, ct).ConfigureAwait(false);
            reachable |= responded;
            if (body is { ValueKind: JsonValueKind.Object } health &&
                health.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String)
            {
                return (true, version.GetString());
            }
        }

        return (reachable, null);
    }

    private async ValueTask<(bool Reachable, HashSet<string>? Paths)> ReadDocPathsAsync(CancellationToken ct)
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

            return (true, normalized);
        }

        return (reachable, null);
    }

    private static OpenCodeRoutes SelectRoutes(HashSet<string>? paths)
    {
        if (paths is null)
        {
            return OpenCodeRoutes.V1;
        }

        if (paths.Contains("/session"))
        {
            return OpenCodeRoutes.V1;
        }

        return paths.Contains("/api/session") ? OpenCodeRoutes.V2 : OpenCodeRoutes.V1;
    }

    private static CapabilitySet ComputeCapabilities(HashSet<string> normalized)
    {
        var capabilities = CapabilitySet.None;

        if (normalized.Contains("/session"))
        {
            capabilities |= CapabilitySet.ListSessions;
        }

        if (normalized.Contains("/session/*/message"))
        {
            capabilities |= CapabilitySet.ReadMessages;
        }

        if (normalized.Contains("/session/*/abort"))
        {
            capabilities |= CapabilitySet.Abort;
        }

        if (normalized.Contains("/session/*/prompt_async"))
        {
            capabilities |= CapabilitySet.PromptAsync | CapabilitySet.Prompt;
        }

        if (normalized.Contains("/config/providers"))
        {
            capabilities |= CapabilitySet.ModelSelect;
        }

        if (normalized.Contains("/session/*/permissions/*"))
        {
            capabilities |= CapabilitySet.Permissions;
        }

        if (normalized.Contains("/event"))
        {
            capabilities |= CapabilitySet.LiveEvents;
        }

        if (normalized.Contains("/session/*/children"))
        {
            capabilities |= CapabilitySet.SubSessions;
        }

        return capabilities;
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
