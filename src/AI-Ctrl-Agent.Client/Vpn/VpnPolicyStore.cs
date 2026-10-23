using System.Text;
using System.Text.Json;
using AiCtrl.Client.Storage;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Vpn;

public sealed class VpnPolicyStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _time;

    private readonly ILogger<VpnPolicyStore> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private VpnPolicyDocument? _document;

    public VpnPolicyStore(
        TimeProvider time,
        ILogger<VpnPolicyStore> logger,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _logger = logger;
        Path = string.IsNullOrWhiteSpace(path) ? ClientPaths.VpnPolicyPath : path;
    }

    public string Path { get; }

    public async Task<VpnPolicyDocument> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VpnPolicyView> GetViewAsync(
        string? currentServer,
        CancellationToken ct)
    {
        var document = await GetAsync(ct).ConfigureAwait(false);
        return document.ToView(_time.GetUtcNow(), currentServer);
    }

    public async Task<VpnPolicyView> SaveAsync(
        VpnPolicyUpdatePayload update,
        string? currentServer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadUnsafeAsync(ct).ConfigureAwait(false);
            var next = (current with
            {
                Enabled = update.Enabled,
                MinLoad = update.MinLoad,
                MaxLoad = update.MaxLoad,
                ExcludeUsedWithinHours = update.ExcludeUsedWithinHours,
                MaxAttemptsPerSession = update.MaxAttemptsPerSession,
                RetryWaitThresholdSeconds = update.RetryWaitThresholdSeconds,
                Countries = update.Countries ?? Array.Empty<string>(),
                UpdatedAt = _time.GetUtcNow(),
            }).Normalize();
            await WriteUnsafeAsync(next, ct).ConfigureAwait(false);
            _document = next;
            return next.ToView(_time.GetUtcNow(), currentServer);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkUsedAsync(
        string server,
        string? country,
        string? city,
        string? exitIp,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        await UpdateServerAsync(
            server,
            state => state with
            {
                Status = VpnServerStatuses.Ok,
                Country = Normalize(country) ?? state.Country,
                City = Normalize(city) ?? state.City,
                LastUsedAt = _time.GetUtcNow(),
                AvailableAt = null,
                LastExitIp = Normalize(exitIp) ?? state.LastExitIp,
                FailureCount = 0,
                LastError = null,
            },
            ct).ConfigureAwait(false);
    }

    public async Task MarkFailureAsync(
        string server,
        string status,
        DateTimeOffset? availableAt,
        string? error,
        string? country,
        string? city,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        await UpdateServerAsync(
            server,
            state => state with
            {
                Status = status,
                Country = Normalize(country) ?? state.Country,
                City = Normalize(city) ?? state.City,
                AvailableAt = availableAt,
                FailureCount = state.FailureCount + 1,
                LastError = Trim(error),
            },
            ct).ConfigureAwait(false);
    }

    private async Task UpdateServerAsync(
        string server,
        Func<VpnServerPolicyState, VpnServerPolicyState> update,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadUnsafeAsync(ct).ConfigureAwait(false);
            var servers = new Dictionary<string, VpnServerPolicyState>(
                current.Servers,
                StringComparer.OrdinalIgnoreCase);
            servers.TryGetValue(server, out var existing);
            servers[server] = update(existing ?? new VpnServerPolicyState());
            var next = (current with
            {
                UpdatedAt = _time.GetUtcNow(),
                Servers = servers,
            }).Normalize();
            await WriteUnsafeAsync(next, ct).ConfigureAwait(false);
            _document = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VpnPolicyDocument> LoadUnsafeAsync(CancellationToken ct)
    {
        if (_document is not null)
        {
            return _document;
        }

        var fallback = VpnPolicyDocument.CreateDefault(_time.GetUtcNow());
        if (!File.Exists(Path))
        {
            _document = fallback;
            return fallback;
        }

        try
        {
            var json = await File.ReadAllTextAsync(Path, Utf8NoBom, ct).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(json, AcJsonContext.Default.VpnPolicyDocument);
            _document = (document ?? fallback).Normalize();
            return _document;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read VPN policy from {Path}; using safe defaults.", Path);
            _document = fallback;
            return fallback;
        }
    }

    private async Task WriteUnsafeAsync(VpnPolicyDocument document, CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(document, AcJsonContext.Default.VpnPolicyDocument);
            await File.WriteAllTextAsync(temp, json, Utf8NoBom, ct).ConfigureAwait(false);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Trim(string? value)
    {
        var normalized = Normalize(value);
        return normalized is { Length: > 400 } ? normalized[..400] : normalized;
    }
}
