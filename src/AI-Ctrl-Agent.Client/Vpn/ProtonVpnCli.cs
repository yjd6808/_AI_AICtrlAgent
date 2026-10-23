using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiCtrl.Client.Vpn;

public sealed record VpnCliStatus(
    bool SignedIn,
    string? Username,
    DateTimeOffset? ExpiresAt,
    bool TunnelUp,
    string? TunnelServer,
    string? TunnelCountry,
    string? TunnelCity,
    string? TunnelDetail,
    string? ExitIp = null);

public sealed record VpnConnectResult(
    string Server,
    string? Country,
    string? CountryName,
    string? City,
    int Load,
    string? ExitIp,
    DateTimeOffset? ConnectedAt);

public sealed record VpnCountry(
    string Code,
    string Name,
    int Count,
    IReadOnlyList<string> Cities);

public sealed record VpnServer(
    string Name,
    string Country,
    string CountryName,
    string? City,
    int Tier,
    string TierName,
    int Load,
    double Score,
    IReadOnlyList<string> Features);

public sealed class ProtonVpnCli
{
    private static readonly Regex ServerId = new(
        "[A-Za-z]{2}#\\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _runner;

    public ProtonVpnCli(IProcessRunner runner, TimeProvider time, string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(time);
        _runner = runner;
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? ResolveExecutable() : executablePath;
    }

    public string? ExecutablePath { get; }

    public bool Found => !string.IsNullOrWhiteSpace(ExecutablePath);

    private string ExecutableName => Path.GetFileName(ExecutablePath) ?? "protonvpn";

    public bool UsesProtonApp => IsAutoExecutable(ExecutablePath);

    public static bool IsAutoExecutable(string? executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        return name is not null
            && name.Contains("protonvpn-auto", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveExecutable()
    {
        var autoPath = Environment.GetEnvironmentVariable("PROTONVPN_AUTO");
        if (!string.IsNullOrWhiteSpace(autoPath) && File.Exists(autoPath))
        {
            return autoPath;
        }

        var overridePath = Environment.GetEnvironmentVariable("PROTONVPN_CLI");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var onPath = FindOnPath("protonvpn.exe")
            ?? FindOnPath("protonvpn.cmd")
            ?? FindOnPath("protonvpn");
        if (onPath is not null)
        {
            return onPath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[]
                 {
                     Path.Combine(home, ".bun", "bin", "protonvpn.exe"),
                     Path.Combine(home, ".bun", "bin", "protonvpn.cmd"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public Task<VpnCliStatus> GetStatusAsync(TimeSpan timeout, CancellationToken ct) =>
        RunAsync("status --json", ParseStatus, timeout, ct);

    public Task<IReadOnlyList<VpnCountry>> GetCountriesAsync(TimeSpan timeout, CancellationToken ct) =>
        RunAsync("countries --json", ParseCountries, timeout, ct);

    public Task<IReadOnlyList<VpnServer>> GetServersAsync(
        string countryCode,
        TimeSpan timeout,
        CancellationToken ct) =>
        RunAsync(
            UsesProtonApp
                ? $"servers {countryCode} --json --limit 2000"
                : $"servers --country {countryCode} --json --limit 2000",
            ParseServers,
            timeout,
            ct);

    public Task<VpnConnectResult> ConnectAsync(
        string serverName,
        TimeSpan timeout,
        CancellationToken ct) =>
        RunAsync(
            UsesProtonApp
                ? $"connect {serverName} --json --timeout {TimeoutSeconds(timeout)}"
                : $"connect {serverName} --json --yes",
            ParseConnect,
            timeout,
            ct);

    public async Task DisconnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        await RunRawAsync(
                UsesProtonApp
                    ? $"disconnect --json --timeout {TimeoutSeconds(timeout)}"
                    : "disconnect --json --yes",
                timeout,
                ct)
            .ConfigureAwait(false);
    }

    private static string TimeoutSeconds(TimeSpan timeout) =>
        Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

    private async Task<T> RunAsync<T>(
        string arguments,
        Func<string, T> parse,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await RunRawAsync(arguments, timeout, ct).ConfigureAwait(false);
        try
        {
            return parse(result.StdOut);
        }
        catch (JsonException ex)
        {
            throw new CliException(
                $"Unexpected output from `{ExecutableName} {arguments}`.{Environment.NewLine}{FirstLines(result.StdOut, 4)}{Environment.NewLine}{ex.Message}",
                result.ExitCode);
        }
    }

    private async Task<ProcessResult> RunRawAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (ExecutablePath is null)
        {
            throw new CliException("protonvpn CLI was not found.", CliExitCode.Error);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        ProcessResult result;
        try
        {
            result = await _runner
                .RunAsync(ExecutablePath, arguments, linkedSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CliException($"{ExecutableName} {arguments} timed out.", CliExitCode.Error);
        }

        if (result.ExitCode != CliExitCode.Ok)
        {
            throw BuildError(arguments, result);
        }

        return result;
    }

    public static VpnCliStatus ParseStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("connected", out var connected)
            && connected.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return ParseAutoStatus(root, connected.ValueKind == JsonValueKind.True);
        }

        var signedIn = root.TryGetProperty("signedIn", out var signed) && signed.ValueKind == JsonValueKind.True;

        var tunnelUp = false;
        string? server = null;
        string? country = null;
        string? city = null;
        string? detail = null;
        if (root.TryGetProperty("tunnel", out var tunnel) && tunnel.ValueKind == JsonValueKind.Object)
        {
            tunnelUp = tunnel.TryGetProperty("up", out var up) && up.ValueKind == JsonValueKind.True;
            server = GetString(tunnel, "server");
            country = GetString(tunnel, "country");
            city = GetString(tunnel, "city");
            detail = GetString(tunnel, "detail");
        }

        return new VpnCliStatus(
            signedIn,
            GetString(root, "username"),
            GetDate(root, "expiresAt"),
            tunnelUp,
            server,
            country,
            city,
            detail);
    }

    private static VpnCliStatus ParseAutoStatus(JsonElement root, bool tunnelUp)
    {
        var serverText = GetString(root, "server");
        var detail = GetString(root, "detail");
        var server = ExtractServerId(serverText) ?? serverText;
        var country = ExtractServerCountry(server) ?? GetString(root, "country");
        var city = ExtractAutoCity(detail ?? serverText);
        return new VpnCliStatus(
            true,
            null,
            null,
            tunnelUp,
            tunnelUp ? server : null,
            tunnelUp ? country : null,
            tunnelUp ? city : null,
            tunnelUp ? detail ?? serverText : null,
            tunnelUp ? GetString(root, "ip") : null);
    }

    private static string? ExtractServerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = ServerId.Match(value);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? ExtractServerCountry(string? server)
    {
        if (string.IsNullOrWhiteSpace(server) || !ServerId.IsMatch(server))
        {
            return null;
        }

        return server[..2].ToUpperInvariant();
    }

    private static string? ExtractAutoCity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 ? value[..separator].Trim() : null;
    }

    public static IReadOnlyList<VpnCountry> ParseCountries(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("countries", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<VpnCountry>();
        }

        var countries = new List<VpnCountry>();
        foreach (var item in array.EnumerateArray())
        {
            var cities = new List<string>();
            if (item.TryGetProperty("cities", out var cityArray) && cityArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var city in cityArray.EnumerateArray())
                {
                    if (city.ValueKind == JsonValueKind.String)
                    {
                        cities.Add(city.GetString()!);
                    }
                }
            }

            countries.Add(new VpnCountry(
                GetString(item, "code") ?? string.Empty,
                GetString(item, "name") ?? string.Empty,
                GetInt(item, "count"),
                cities));
        }

        return countries;
    }

    public static IReadOnlyList<VpnServer> ParseServers(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("servers", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<VpnServer>();
        }

        string? rootCountryName = null;
        if (root.TryGetProperty("country", out var rootCountry)
            && rootCountry.ValueKind == JsonValueKind.Object)
        {
            rootCountryName = GetString(rootCountry, "name");
        }

        var servers = new List<VpnServer>();
        foreach (var item in array.EnumerateArray())
        {
            var features = new List<string>();
            if (item.TryGetProperty("features", out var featureArray)
                && featureArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in featureArray.EnumerateArray())
                {
                    if (feature.ValueKind == JsonValueKind.String)
                    {
                        features.Add(feature.GetString()!);
                    }
                }
            }

            var tierName = GetString(item, "tierName");
            var tier = GetInt(item, "tier");
            if (item.TryGetProperty("tier", out var tierValue) && tierValue.ValueKind == JsonValueKind.String)
            {
                tierName ??= tierValue.GetString();
                tier = TierNumber(tierName);
            }

            servers.Add(new VpnServer(
                GetString(item, "name") ?? string.Empty,
                GetString(item, "country") ?? string.Empty,
                GetString(item, "countryName") ?? rootCountryName ?? string.Empty,
                GetString(item, "city"),
                tier,
                tierName ?? string.Empty,
                GetInt(item, "load"),
                GetDouble(item, "score"),
                features));
        }

        return servers;
    }

    private static int TierNumber(string? tierName) =>
        tierName?.Trim().ToUpperInvariant() switch
        {
            "FREE" => 0,
            "BASIC" => 1,
            "PLUS" => 2,
            "UNLIMITED" => 3,
            _ => 0,
        };

    public static VpnConnectResult ParseConnect(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("connected", out var connected))
        {
            throw new JsonException("Response did not contain a 'connected' value.");
        }

        if (connected.ValueKind == JsonValueKind.Object)
        {
            return new VpnConnectResult(
                GetString(connected, "server") ?? string.Empty,
                GetString(connected, "country"),
                GetString(connected, "countryName"),
                GetString(connected, "city"),
                GetInt(connected, "load"),
                GetString(connected, "exitIp"),
                GetDate(connected, "connectedAt"));
        }

        if (connected.ValueKind != JsonValueKind.True)
        {
            throw new JsonException("The VPN connection was not established.");
        }

        var serverText = GetString(root, "server");
        var server = ExtractServerId(serverText) ?? serverText ?? string.Empty;
        var detail = GetString(root, "detail") ?? serverText;
        return new VpnConnectResult(
            server,
            ExtractServerCountry(server),
            GetString(root, "country"),
            ExtractAutoCity(detail),
            0,
            GetString(root, "ip"),
            null);
    }

    private static CliException BuildError(string arguments, ProcessResult result)
    {
        var message = TryReadErrorField(result.StdErr)
            ?? TryReadErrorField(result.StdOut)
            ?? FirstNonEmpty(result.StdErr, result.StdOut)
            ?? $"protonvpn {arguments} failed with exit code {result.ExitCode}.";
        return new CliException(message.Trim(), result.ExitCode);
    }

    private static string? TryReadErrorField(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                var value = error.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullPath = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static double GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0d;

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static string? FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no output)";
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Take(count));
    }
}
