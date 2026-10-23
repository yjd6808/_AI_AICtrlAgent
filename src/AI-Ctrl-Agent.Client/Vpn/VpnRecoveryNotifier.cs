using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Vpn;

public enum VpnRecoveryNoticeKind
{
    QuotaExceeded,
    RateLimited,
    Stalled,
    VpnConnected,
    Resumed,
    Failed,
    Exhausted,
}

public sealed record VpnRecoveryNotice(
    VpnRecoveryNoticeKind Kind,
    string Machine,
    SessionRef Session,
    string? Server = null,
    string? ErrorCode = null,
    TimeSpan? StalledFor = null);

public interface IVpnRecoveryNotifier
{
    Task SendAsync(VpnRecoveryNotice notice, CancellationToken ct);
}

public sealed class DiscordVpnRecoveryNotifier : IVpnRecoveryNotifier
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly Uri? _webhook;

    private readonly HttpClient _client;

    private readonly ILogger<DiscordVpnRecoveryNotifier> _logger;

    public DiscordVpnRecoveryNotifier(
        VpnRecoveryOptions options,
        ILogger<DiscordVpnRecoveryNotifier> logger)
        : this(options, logger, SharedClient)
    {
    }

    public DiscordVpnRecoveryNotifier(
        VpnRecoveryOptions options,
        ILogger<DiscordVpnRecoveryNotifier> logger,
        HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(client);
        _webhook = options.WebhookUri;
        _logger = logger;
        _client = client;
    }

    public async Task SendAsync(VpnRecoveryNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_webhook is null)
        {
            return;
        }

        var payload = new DiscordWebhookPayload(
            Format(notice),
            new DiscordAllowedMentions(Array.Empty<string>()));
        var json = JsonSerializer.Serialize(payload, VpnRecoveryJsonContext.Default.DiscordWebhookPayload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync(_webhook, content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Discord recovery webhook request failed with {ExceptionType}.",
                ex.GetType().Name);
            throw new InvalidOperationException("Discord recovery webhook request failed.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Discord recovery webhook returned status {StatusCode}.",
                    (int)response.StatusCode);
                throw new InvalidOperationException(
                    $"Discord recovery webhook returned status {(int)response.StatusCode}.");
            }
        }
    }

    private static string Format(VpnRecoveryNotice notice)
    {
        var target = $"{notice.Machine} / {notice.Session.Instance.Value} / {notice.Session.SessionId}";
        return notice.Kind switch
        {
            VpnRecoveryNoticeKind.QuotaExceeded => $"[AI-Ctrl] {target}: 할당량 소진 감지",
            VpnRecoveryNoticeKind.RateLimited => $"[AI-Ctrl] {target}: API rate limit 감지",
            VpnRecoveryNoticeKind.Stalled =>
                $"[AI-Ctrl] {target}: {Math.Max(1, (int)Math.Round(notice.StalledFor?.TotalMinutes ?? 0))}분 정체",
            VpnRecoveryNoticeKind.VpnConnected =>
                $"[AI-Ctrl] {target}: VPN 전환 완료 ({notice.Server ?? "unknown"})",
            VpnRecoveryNoticeKind.Resumed => $"[AI-Ctrl] {target}: 세션 재개 완료",
            VpnRecoveryNoticeKind.Failed =>
                $"[AI-Ctrl] {target}: 복구 실패 ({notice.ErrorCode ?? "internal_error"})",
            VpnRecoveryNoticeKind.Exhausted => $"[AI-Ctrl] {target}: 복구 시도 한도 초과",
            _ => $"[AI-Ctrl] {target}: VPN recovery 알림",
        };
    }
}

internal sealed record DiscordWebhookPayload(
    string Content,
    [property: JsonPropertyName("allowed_mentions")] DiscordAllowedMentions AllowedMentions);

internal sealed record DiscordAllowedMentions(string[] Parse);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DiscordWebhookPayload))]
internal sealed partial class VpnRecoveryJsonContext : JsonSerializerContext;
