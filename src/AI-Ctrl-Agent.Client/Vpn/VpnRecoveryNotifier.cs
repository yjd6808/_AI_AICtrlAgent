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
    Network,
    AuthFailed,
    ModelUnavailable,
    Unknown,
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

    private readonly VpnPolicyStore? _policy;

    private readonly VpnRecoveryOptions? _options;

    private readonly VpnRecoverySettings _staticSettings;

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
        : this((VpnPolicyStore?)null, options, logger, client, marker: true)
    {
    }

    public DiscordVpnRecoveryNotifier(
        VpnPolicyStore policy,
        VpnRecoveryOptions options,
        ILogger<DiscordVpnRecoveryNotifier> logger,
        HttpClient? client = null)
        : this(policy, options, logger, client ?? SharedClient, marker: true)
    {
    }

    private DiscordVpnRecoveryNotifier(
        VpnPolicyStore? policy,
        VpnRecoveryOptions options,
        ILogger<DiscordVpnRecoveryNotifier> logger,
        HttpClient client,
        bool marker)
    {
        _ = marker;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(client);
        _policy = policy;
        _options = options;
        _staticSettings = options.ToSettings() with
        {
            DiscordEnabled = options.DiscordEnabled || options.WebhookUri is not null,
        };
        _logger = logger;
        _client = client;
    }

    public async Task SendAsync(VpnRecoveryNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var settings = _policy is null
            ? _staticSettings.Normalize()
            : await _policy.GetRecoveryAsync(ct).ConfigureAwait(false);
        if (!settings.DiscordEnabled)
        {
            return;
        }

        if (settings.DiscordNotifyOnFailureOnly
            && notice.Kind is not (VpnRecoveryNoticeKind.Failed or VpnRecoveryNoticeKind.Exhausted))
        {
            return;
        }

        var webhook = await ResolveWebhookAsync(ct).ConfigureAwait(false);
        if (webhook is null)
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
            response = await _client.PostAsync(webhook, content, ct).ConfigureAwait(false);
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

    private async Task<Uri?> ResolveWebhookAsync(CancellationToken ct)
    {
        if (_policy is not null)
        {
            var stored = await _policy.GetDiscordWebhookAsync(ct).ConfigureAwait(false);
            return VpnRecoverySettings.TryParseDiscordWebhook(stored, out var parsed) ? parsed : null;
        }

        return _options?.WebhookUri;
    }

    private static string Format(VpnRecoveryNotice notice)
    {
        var target = $"{notice.Machine} / {notice.Session.Instance.Value} / {notice.Session.SessionId}";
        return notice.Kind switch
        {
            VpnRecoveryNoticeKind.QuotaExceeded => $"[AI-Ctrl] {target}: 무료 할당량 소진 감지",
            VpnRecoveryNoticeKind.RateLimited => $"[AI-Ctrl] {target}: API 요청 한도(429) 감지",
            VpnRecoveryNoticeKind.AuthFailed => $"[AI-Ctrl] {target}: 인증 실패 감지",
            VpnRecoveryNoticeKind.ModelUnavailable => $"[AI-Ctrl] {target}: 모델 사용 불가 감지",
            VpnRecoveryNoticeKind.Network => $"[AI-Ctrl] {target}: 네트워크 오류 감지",
            VpnRecoveryNoticeKind.Unknown => $"[AI-Ctrl] {target}: 알 수 없는 오류 감지",
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
