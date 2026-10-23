using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Runtime;
using AiCtrl.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Vpn;

public sealed partial class VpnSystem : IAcSystem
{
    private static readonly TimeSpan ServerCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(1);

    private readonly VpnOptions _options;

    private readonly OutboxStore _outbox;

    private readonly TimeProvider _time;

    private readonly ILogger<VpnSystem> _logger;

    private readonly StateSystem? _state;

    private readonly ProtonVpnCli _cli;

    private readonly ConnectionHistory _history;

    private readonly Func<bool> _protonGuiRunning;

    private readonly VpnHelperClient? _helper;

    private readonly IVpnReadinessProbe _readinessProbe;

    private readonly Func<bool>? _serverLinkConnected;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private readonly SemaphoreSlim _serverCacheGate = new(1, 1);

    private readonly object _statusGate = new();

    private readonly Dictionary<string, ServerCacheEntry> _serverCache =
        new(StringComparer.OrdinalIgnoreCase);

    private AcSystemContext? _context;

    private VpnStatusView _status;

    private string _lastPublished = string.Empty;

    private string? _busyOperation;

    private string? _busyCommandId;

    private string? _lastExitIp;

    public VpnSystem(
        VpnOptions options,
        OutboxStore outbox,
        TimeProvider time,
        ILogger<VpnSystem> logger,
        StateSystem? state = null,
        ProtonVpnCli? cli = null,
        ConnectionHistory? history = null,
        Func<bool>? protonGuiRunning = null,
        VpnHelperClient? helper = null,
        IVpnReadinessProbe? readinessProbe = null,
        Func<bool>? serverLinkConnected = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _outbox = outbox;
        _time = time;
        _logger = logger;
        _state = state;
        _helper = helper;
        _readinessProbe = readinessProbe ?? new HttpVpnReadinessProbe();
        _serverLinkConnected = serverLinkConnected;
        if (cli is not null)
        {
            _cli = cli;
        }
        else
        {
            var executablePath = string.IsNullOrWhiteSpace(options.CliPath)
                ? ProtonVpnCli.ResolveExecutable()
                : options.CliPath;
            IProcessRunner runner = ProtonVpnCli.IsAutoExecutable(executablePath)
                ? new ProcessRunner()
                : new HelperVpnProcessRunner(new ProcessRunner(), helper);
            _cli = new ProtonVpnCli(runner, time, executablePath);
        }
        _history = history ?? new ConnectionHistory(options.HistoryPath, time);
        _protonGuiRunning = protonGuiRunning ?? IsProtonGuiRunning;
        _status = new VpnStatusView(
            _cli.Found,
            _cli.ExecutablePath,
            false,
            false,
            false,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            options.Enabled ? "VPN 상태를 확인하는 중입니다." : "VPN이 비활성화되어 있습니다.",
            null,
            null,
            time.GetUtcNow(),
            _cli.UsesProtonApp);
    }

    public string Name => "vpn";

    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    public bool CliFound => _cli.Found;

    public bool Enabled => _options.Enabled;

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _context = ctx;
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await RefreshAndPublishAsync(ct).ConfigureAwait(false);
        _context?.Loops.Run("vpn.poll", PollAsync);
    }

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public async Task<VpnStatusView> GetStatusAsync(CancellationToken ct)
    {
        await RefreshAndPublishAsync(ct).ConfigureAwait(false);
        return Snapshot();
    }

    public async Task<IReadOnlyList<VpnCountryView>> GetCountriesAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, "VPN is disabled in client configuration.");
        }

        if (!_cli.Found)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, "protonvpn CLI was not found.");
        }

        try
        {
            var countries = await _cli.GetCountriesAsync(_options.QueryTimeout, ct).ConfigureAwait(false);
            return countries
                .Select(country => new VpnCountryView(country.Code, country.Name, country.Count, country.Cities))
                .ToArray();
        }
        catch (CliException ex)
        {
            throw Map(ex);
        }
    }

    public async Task<VpnPageSnapshot> GetPageAsync(VpnPageQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.CliFound)
        {
            return new VpnPageSnapshot(
                status,
                Array.Empty<VpnCountryView>(),
                string.Empty,
                null,
                "protonvpn CLI를 찾을 수 없습니다. `protonvpn` 설치 후 다시 시도하세요.");
        }

        IReadOnlyList<VpnCountryView> countries;
        try
        {
            countries = await GetCountriesAsync(ct).ConfigureAwait(false);
        }
        catch (VpnException ex)
        {
            return new VpnPageSnapshot(status, Array.Empty<VpnCountryView>(), string.Empty, null, ex.Message);
        }

        var selected = ResolveCountry(query.Country, status, countries);
        if (!status.SignedIn)
        {
            return new VpnPageSnapshot(status, countries, selected, null, SignInText());
        }

        var page = await TryGetServerPageAsync(selected, query.PrevCity, refresh: false, ct)
            .ConfigureAwait(false);
        return new VpnPageSnapshot(status, countries, selected, page, ActionText(status));
    }

    public async Task<VpnServerPage> GetServersAsync(VpnServersQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_options.Enabled)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, "VPN is disabled in client configuration.");
        }

        var country = NormalizeCountry(query.Country);
        if (country is null)
        {
            throw new VpnException(AcErrorCodes.InvalidPayload, "A two-letter VPN country code is required.");
        }

        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.CliFound)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, "protonvpn CLI was not found.");
        }

        if (!status.SignedIn)
        {
            throw new VpnException(AcErrorCodes.VpnSigninRequired, SignInText());
        }

        return await GetServerPageAsync(country!, query.PrevCity, query.Refresh, ct).ConfigureAwait(false);
    }

    public async Task<VpnActionSnapshot> ConnectAsync(
        VpnConnectPayload payload,
        string commandId,
        CancellationToken ct,
        Func<VpnProgress, Task>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_options.Enabled)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, "VPN is disabled in client configuration.");
        }

        if (!TryBeginBusy("connect", commandId))
        {
            throw new VpnException(AcErrorCodes.VpnBusy, "Another VPN operation is already running.");
        }

        try
        {
            await PublishIfChangedAsync(ct).ConfigureAwait(false);
            await RefreshStatusAsync(ct).ConfigureAwait(false);
            var status = Snapshot();
            if (!status.CliFound)
            {
                throw new VpnException(AcErrorCodes.CapabilityMissing, "protonvpn CLI was not found.");
            }

            if (!status.SignedIn)
            {
                throw new VpnException(AcErrorCodes.VpnSigninRequired, SignInText());
            }

            if (!_cli.UsesProtonApp && _protonGuiRunning())
            {
                throw new VpnException(AcErrorCodes.ProtonGuiRunning, "Proton VPN GUI is running. Close it before connecting.");
            }

            EnsureSessionsIdle(payload.Force);
            var target = await ResolveTargetAsync(payload, ct).ConfigureAwait(false);
            await ReportProgressAsync(
                onProgress,
                "switching",
                JsonSerializer.SerializeToElement(new Dictionary<string, string?>
                {
                    ["server"] = target.Server,
                    ["country"] = target.Country,
                }),
                ct).ConfigureAwait(false);

            VpnConnectResult result;
            try
            {
                result = await _cli.ConnectAsync(target.Server, _options.ConnectTimeout, ct).ConfigureAwait(false);
            }
            catch (CliException ex)
            {
                throw Map(ex);
            }

            var connectedAt = result.ConnectedAt ?? _time.GetUtcNow();
            _lastExitIp = result.ExitIp;
            try
            {
                await RefreshStatusAsync(ct).ConfigureAwait(false);
                await ReportProgressAsync(
                    onProgress,
                    "waiting_tunnel",
                    JsonSerializer.SerializeToElement(new Dictionary<string, string?>
                    {
                        ["mode"] = _options.Fallback.Mode.ToString(),
                        ["timeoutSeconds"] = ((int)_options.Fallback.Timeout.TotalSeconds)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }),
                    ct).ConfigureAwait(false);
                await EnsureFallbackAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryDisconnectForFallbackAsync().ConfigureAwait(false);
                throw;
            }

            _history.Record(result.Server, connectedAt);
            var updated = Snapshot();
            var servers = await TryGetServerPageAsync(
                NormalizeCountry(result.Country) ?? target.Country ?? _options.DefaultCountry,
                payload.City,
                refresh: true,
                ct).ConfigureAwait(false);
            var actionText = ConnectText(result);
            return new VpnActionSnapshot(
                new VpnConnectInfo(
                    result.Server,
                    result.Country,
                    result.CountryName,
                    result.City,
                    result.Load,
                    result.ExitIp,
                    connectedAt),
                actionText,
                updated,
                servers);
        }
        finally
        {
            ClearBusy();
            await PublishBestEffortAsync().ConfigureAwait(false);
        }
    }

    public async Task<VpnActionSnapshot> DisconnectAsync(string commandId, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, "VPN is disabled in client configuration.");
        }

        if (!TryBeginBusy("disconnect", commandId))
        {
            throw new VpnException(AcErrorCodes.VpnBusy, "Another VPN operation is already running.");
        }

        try
        {
            await PublishIfChangedAsync(ct).ConfigureAwait(false);
            if (!_cli.Found)
            {
                throw new VpnException(AcErrorCodes.CapabilityMissing, "protonvpn CLI was not found.");
            }

            try
            {
                await _cli.DisconnectAsync(_options.QueryTimeout, ct).ConfigureAwait(false);
            }
            catch (CliException ex)
            {
                throw Map(ex);
            }

            _lastExitIp = null;
            await RefreshStatusAsync(ct).ConfigureAwait(false);
            var status = Snapshot();
            var servers = status.Country is null
                ? null
                : await TryGetServerPageAsync(
                    NormalizeCountry(status.Country) ?? _options.DefaultCountry,
                    status.City,
                    refresh: true,
                    ct).ConfigureAwait(false);
            return new VpnActionSnapshot(null, "VPN 연결을 해제했습니다.", status, servers);
        }
        finally
        {
            ClearBusy();
            await PublishBestEffortAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.StatusPollInterval, _time, ct).ConfigureAwait(false);
            if (CurrentBusyOperation() is not null)
            {
                continue;
            }

            await RefreshAndPublishAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAndPublishAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await RefreshStatusAsync(ct).ConfigureAwait(false);
        await PublishIfChangedAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshStatusAsync(CancellationToken ct)
    {
        if (!_cli.Found)
        {
            SetStatus(_status with
            {
                CliFound = false,
                CliPath = null,
                HelperAvailable = false,
                AsOf = _time.GetUtcNow(),
                StatusText = "protonvpn CLI를 찾을 수 없습니다.",
            });
            return;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var helperAvailable = await HelperAvailableAsync(ct).ConfigureAwait(false);
            VpnCliStatus cliStatus;
            try
            {
                cliStatus = await _cli.GetStatusAsync(_options.QueryTimeout, ct).ConfigureAwait(false);
            }
            catch (CliException ex)
            {
                var now = _time.GetUtcNow();
                SetStatus(_status with
                {
                    CliFound = true,
                    CliPath = _cli.ExecutablePath,
                    HelperAvailable = helperAvailable,
                    SignedIn = false,
                    TunnelUp = false,
                    StatusText = ex.Message,
                    AsOf = now,
                });
                return;
            }

            var exitIp = cliStatus.TunnelUp ? cliStatus.ExitIp ?? _lastExitIp : null;
            var status = new VpnStatusView(
                true,
                _cli.ExecutablePath,
                helperAvailable,
                _protonGuiRunning(),
                cliStatus.SignedIn,
                cliStatus.Username,
                cliStatus.ExpiresAt,
                cliStatus.TunnelUp,
                cliStatus.TunnelServer,
                cliStatus.TunnelCountry,
                cliStatus.TunnelCity,
                cliStatus.TunnelDetail,
                exitIp,
                BuildStatusText(cliStatus),
                CurrentBusyOperation(),
                CurrentBusyCommandId(),
                _time.GetUtcNow(),
                _cli.UsesProtonApp);
            SetStatus(status);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<VpnServerPage> GetServerPageAsync(
        string country,
        string? previousCity,
        bool refresh,
        CancellationToken ct)
    {
        var countries = await GetCountriesAsync(ct).ConfigureAwait(false);
        var countryView = countries.FirstOrDefault(item => string.Equals(
            item.Code,
            country,
            StringComparison.OrdinalIgnoreCase));
        if (countryView is null)
        {
            throw new VpnException(AcErrorCodes.InvalidPayload, $"Unknown VPN country '{country}'.");
        }

        var servers = await GetServersCachedAsync(country, refresh, ct).ConfigureAwait(false);
        var status = Snapshot();
        return VpnServerListBuilder.Build(
            new VpnCountry(countryView.Code, countryView.Name, countryView.Count, countryView.Cities),
            servers,
            status,
            _history,
            _time.GetUtcNow(),
            previousCity);
    }

    private async Task<VpnServerPage?> TryGetServerPageAsync(
        string country,
        string? previousCity,
        bool refresh,
        CancellationToken ct)
    {
        try
        {
            return await GetServerPageAsync(country, previousCity, refresh, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CliException or VpnException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Could not build VPN server page for {Country}.", country);
            return null;
        }
    }

    private async Task<IReadOnlyList<VpnServer>> GetServersCachedAsync(
        string country,
        bool refresh,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!refresh)
        {
            await _serverCacheGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_serverCache.TryGetValue(country, out var cached)
                    && now - cached.LoadedAt < ServerCacheTtl)
                {
                    return cached.Servers;
                }
            }
            finally
            {
                _serverCacheGate.Release();
            }
        }

        IReadOnlyList<VpnServer> servers;
        try
        {
            servers = await _cli.GetServersAsync(country, _options.QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            throw Map(ex);
        }

        await _serverCacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _serverCache[country] = new ServerCacheEntry(now, servers);
        }
        finally
        {
            _serverCacheGate.Release();
        }

        return servers;
    }

    private async Task<(string Server, string? Country)> ResolveTargetAsync(
        VpnConnectPayload payload,
        CancellationToken ct)
    {
        var explicitServer = payload.Server?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitServer))
        {
            if (!ServerNamePattern().IsMatch(explicitServer))
            {
                throw new VpnException(AcErrorCodes.InvalidPayload, "Invalid VPN server name.");
            }

            var requestedCountry = NormalizeCountry(payload.Country);
            await EnsureKnownServerAsync(explicitServer, requestedCountry, ct).ConfigureAwait(false);
            return (explicitServer, requestedCountry);
        }

        var country = NormalizeCountry(payload.Country) ?? _options.DefaultCountry;
        if (!CountryPattern().IsMatch(country))
        {
            throw new VpnException(AcErrorCodes.InvalidPayload, "Country must be a two-letter code.");
        }

        var servers = await GetServersCachedAsync(country, refresh: true, ct).ConfigureAwait(false);
        var filtered = servers;
        if (!string.IsNullOrWhiteSpace(payload.City))
        {
            var city = payload.City.Trim();
            var matching = servers
                .Where(server => string.Equals(server.City, city, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matching.Length > 0)
            {
                filtered = matching;
            }
        }

        var selected = filtered
            .OrderBy(server => server.Load)
            .ThenByDescending(server => server.Score)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selected is null)
        {
            throw new VpnException(AcErrorCodes.CapabilityMissing, $"No VPN server is available for '{country}'.");
        }

        if (!ServerNamePattern().IsMatch(selected.Name))
        {
            throw new VpnException(AcErrorCodes.InternalError, "The VPN server list contained an invalid server name.");
        }

        return (selected.Name, country);
    }

    private async Task EnsureKnownServerAsync(string server, string? country, CancellationToken ct)
    {
        var candidates = new List<string>();
        await _serverCacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in _serverCache)
            {
                if (!entry.Value.Servers.Any(candidate => string.Equals(
                        candidate.Name,
                        server,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidates.Add(entry.Key);
            }
        }
        finally
        {
            _serverCacheGate.Release();
        }

        if (country is not null)
        {
            candidates.Insert(0, country);
        }

        candidates.Add(_options.DefaultCountry);
        foreach (var code in candidates
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(6))
        {
            IReadOnlyList<VpnServer> servers;
            try
            {
                servers = await GetServersCachedAsync(code, refresh: true, ct).ConfigureAwait(false);
            }
            catch (VpnException)
            {
                continue;
            }

            if (servers.Any(candidate => string.Equals(
                    candidate.Name,
                    server,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        throw new VpnException(
            AcErrorCodes.InvalidPayload,
            $"VPN server '{server}' is not in the current server list.");
    }

    private async Task EnsureFallbackAsync(CancellationToken ct)
    {
        var fallback = _options.Fallback;
        if (fallback.Mode == VpnFallbackMode.ServerLink)
        {
            if (await WaitForServerLinkAsync(fallback.Timeout, ct).ConfigureAwait(false))
            {
                return;
            }

            await FailFallbackAsync(
                "The server link did not reconnect within the VPN fallback window.")
                .ConfigureAwait(false);
        }

        if (await WaitForTunnelReadyAsync(fallback, ct).ConfigureAwait(false))
        {
            return;
        }

        await FailFallbackAsync(
            $"The VPN tunnel did not become ready within {FormatDuration(fallback.Timeout)}.")
            .ConfigureAwait(false);
    }

    private async Task<bool> WaitForTunnelReadyAsync(
        VpnFallbackOptions fallback,
        CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + fallback.Timeout;
        while (true)
        {
            if (await _readinessProbe
                    .IsReadyAsync(fallback.ReadyUri, ct)
                    .ConfigureAwait(false))
            {
                return true;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining < FallbackPollInterval ? remaining : FallbackPollInterval,
                _time,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForServerLinkAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_serverLinkConnected is null)
        {
            return false;
        }

        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            if (_serverLinkConnected())
            {
                return true;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining < FallbackPollInterval ? remaining : FallbackPollInterval,
                _time,
                ct).ConfigureAwait(false);
        }
    }

    private async Task FailFallbackAsync(string message)
    {
        _logger.LogWarning("VPN fallback failed: {Message}", message);
        var disconnected = await TryDisconnectForFallbackAsync().ConfigureAwait(false);

        if (_options.Fallback.Mode == VpnFallbackMode.TunnelReady)
        {
            try
            {
                using var source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var restored = await _readinessProbe
                    .IsReadyAsync(_options.Fallback.ReadyUri, source.Token)
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    "VPN fallback disconnected the tunnel; ready endpoint restored: {Restored}.",
                    restored);
            }
            catch (OperationCanceledException)
            {
            }
        }

        throw new VpnException(
            AcErrorCodes.InternalError,
            disconnected
                ? $"{message} The VPN was disconnected to protect remote access."
                : $"{message} The VPN could not be disconnected; remote access may be unavailable.");
    }

    private async Task<bool> TryDisconnectForFallbackAsync()
    {
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _cli.DisconnectAsync(_options.QueryTimeout, source.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CliException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "VPN fallback could not disconnect the tunnel.");
            return false;
        }

        _lastExitIp = null;
        try
        {
            await RefreshStatusAsync(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return true;
    }

    private async Task ReportProgressAsync(
        Func<VpnProgress, Task>? onProgress,
        string step,
        JsonElement? data,
        CancellationToken ct)
    {
        if (onProgress is null)
        {
            return;
        }

        try
        {
            await onProgress(new VpnProgress(step, data)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not report VPN progress step {Step}.", step);
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#} minutes"
            : $"{duration.TotalSeconds:0.#} seconds";

    private void EnsureSessionsIdle(bool force)
    {
        if (force || _state is null)
        {
            return;
        }

        var busy = _state.GetSnapshots().Any(snapshot => snapshot.DisplayState is
            SessionDisplayState.Running
            or SessionDisplayState.Retrying
            or SessionDisplayState.WaitingPermission
            or SessionDisplayState.Stalled
            or SessionDisplayState.ForeignBusy);
        if (busy)
        {
            throw new VpnException(
                AcErrorCodes.SessionsBusy,
                "A session is currently busy. Retry with force to switch VPN.");
        }
    }

    private void SetStatus(VpnStatusView status)
    {
        lock (_statusGate)
        {
            _status = status;
        }
    }

    private VpnStatusView Snapshot()
    {
        lock (_statusGate)
        {
            return _status;
        }
    }

    private bool TryBeginBusy(string operation, string commandId)
    {
        lock (_statusGate)
        {
            if (_busyOperation is not null)
            {
                return false;
            }

            _busyOperation = operation;
            _busyCommandId = commandId;
            _status = _status with
            {
                BusyOperation = operation,
                BusyCommandId = commandId,
                AsOf = _time.GetUtcNow(),
                StatusText = $"VPN 작업 중입니다: {operation}",
            };
            return true;
        }
    }

    private void ClearBusy()
    {
        lock (_statusGate)
        {
            _busyOperation = null;
            _busyCommandId = null;
            _status = _status with
            {
                BusyOperation = null,
                BusyCommandId = null,
                AsOf = _time.GetUtcNow(),
            };
        }
    }

    private async Task<bool> HelperAvailableAsync(CancellationToken ct)
    {
        if (_helper is null)
        {
            return false;
        }

        try
        {
            return await _helper.IsAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private string? CurrentBusyOperation()
    {
        lock (_statusGate)
        {
            return _busyOperation;
        }
    }

    private string? CurrentBusyCommandId()
    {
        lock (_statusGate)
        {
            return _busyCommandId;
        }
    }

    private async Task PublishIfChangedAsync(CancellationToken ct)
    {
        await _publishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = Snapshot();
            var comparable = status with
            {
                AsOf = default,
            };
            var key = JsonSerializer.Serialize(comparable, AcJsonContext.Default.VpnStatusView);
            if (string.Equals(key, _lastPublished, StringComparison.Ordinal))
            {
                return;
            }

            await _outbox
                .AppendAsync(
                    AgentEventTypes.VpnState,
                    JsonSerializer.Serialize(status, AcJsonContext.Default.VpnStatusView),
                    ct)
                .ConfigureAwait(false);
            _lastPublished = key;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "Could not publish VPN state.");
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task PublishBestEffortAsync()
    {
        try
        {
            using var source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PublishIfChangedAsync(source.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "Could not publish final VPN state.");
        }
    }

    private string ResolveCountry(
        string? requested,
        VpnStatusView status,
        IReadOnlyList<VpnCountryView> countries)
    {
        var normalized = NormalizeCountry(requested);
        if (normalized is not null
            && countries.Any(country => string.Equals(country.Code, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return normalized;
        }

        var connected = countries.FirstOrDefault(country =>
            string.Equals(country.Code, status.Country, StringComparison.OrdinalIgnoreCase)
            || string.Equals(country.Name, status.Country, StringComparison.OrdinalIgnoreCase));
        if (connected is not null)
        {
            return connected.Code;
        }

        var fallback = countries.FirstOrDefault(country => string.Equals(
            country.Code,
            _options.DefaultCountry,
            StringComparison.OrdinalIgnoreCase));
        return fallback?.Code ?? countries.FirstOrDefault()?.Code ?? _options.DefaultCountry;
    }

    private string BuildStatusText(VpnCliStatus status)
    {
        if (CurrentBusyOperation() is { } operation)
        {
            return $"VPN 작업 중입니다: {operation}";
        }

        if (!status.SignedIn)
        {
            return SignInText();
        }

        var expires = status.ExpiresAt is null
            ? string.Empty
            : $" · 만료 {status.ExpiresAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
        var tunnel = status.TunnelUp
            ? $" · 터널 연결됨 ({status.TunnelServer})"
            : " · 터널 꺼짐";
        var account = string.IsNullOrWhiteSpace(status.Username)
            ? "Proton VPN 앱"
            : $"로그인됨: {status.Username}";
        return $"{account}{expires}{tunnel}";
    }

    private static string ActionText(VpnStatusView status)
    {
        if (status.BusyOperation is { } operation)
        {
            return $"VPN 작업 중입니다: {operation}";
        }

        if (!status.SignedIn)
        {
            return SignInText();
        }

        return status.TunnelUp ? $"VPN 연결됨: {status.Server}" : "VPN 연결이 해제되어 있습니다.";
    }

    private static string SignInText() =>
        "PC 터미널에서 `protonvpn signin`을 실행해 로그인하세요.";

    private static string ConnectText(VpnConnectResult result)
    {
        var location = string.Join(
            " · ",
            new[] { result.City, result.CountryName ?? result.Country }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var exitIp = string.IsNullOrWhiteSpace(result.ExitIp) ? string.Empty : $" · exitIp {result.ExitIp}";
        var connectedAt = result.ConnectedAt ?? DateTimeOffset.UtcNow;
        return $"연결됨: {result.Server}{location}{exitIp} · {connectedAt.ToLocalTime():HH:mm:ss}";
    }

    private VpnException Map(CliException exception)
    {
        var code = exception.ExitCode switch
        {
            CliExitCode.NotSignedIn => AcErrorCodes.VpnSigninRequired,
            CliExitCode.Privilege => AcErrorCodes.VpnPrivilege,
            _ => AcErrorCodes.InternalError,
        };
        return new VpnException(code, exception.Message);
    }

    private static string? NormalizeCountry(string? country)
    {
        var value = country?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsProtonGuiRunning()
    {
        foreach (var name in new[] { "ProtonVPN.Client", "ProtonVPN", "Proton VPN", "ProtonVPN.GUI" })
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0)
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    process.Dispose();
                }

                return true;
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        return false;
    }

    private sealed record ServerCacheEntry(DateTimeOffset LoadedAt, IReadOnlyList<VpnServer> Servers);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9#._-]{0,63}$")]
    private static partial Regex ServerNamePattern();

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex CountryPattern();
}
