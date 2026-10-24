using AiCtrl.Client.Providers.OpenCode.Backend;
using AiCtrl.Client.Vpn;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class VpnPolicyTests
{
    [Fact]
    public async Task Store_uses_defaults_when_file_is_missing_or_corrupt()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "vpn-policy.json");
        var time = new FakeTimeProvider();
        var store = new VpnPolicyStore(time, NullLogger<VpnPolicyStore>.Instance, path);

        var missing = await store.GetAsync(TestContext.Current.CancellationToken);
        missing.Enabled.ShouldBeTrue();
        missing.MaxLoad.ShouldBe(70);
        missing.MaxAttemptsPerSession.ShouldBe(3);

        await File.WriteAllTextAsync(path, "{not json", TestContext.Current.CancellationToken);
        var corrupt = new VpnPolicyStore(
            time,
            NullLogger<VpnPolicyStore>.Instance,
            path).GetAsync(TestContext.Current.CancellationToken);
        (await corrupt).Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_persists_normalized_policy_and_server_state()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "vpn-policy.json");
        var time = new FakeTimeProvider();
        var store = new VpnPolicyStore(time, NullLogger<VpnPolicyStore>.Instance, path);

        await store.SaveAsync(
            new VpnPolicyUpdatePayload(
                Enabled: true,
                MinLoad: 80,
                MaxLoad: 10,
                ExcludeUsedWithinHours: 48,
                MaxAttemptsPerSession: 4,
                RetryWaitThresholdSeconds: 240,
                Countries: ["jp", "JP", " us "]),
            currentServer: null,
            TestContext.Current.CancellationToken);
        await store.MarkFailureAsync(
            "JP#10",
            VpnServerStatuses.QuotaExhausted,
            time.GetUtcNow().AddHours(2),
            "Free usage exceeded",
            "JP",
            "Tokyo",
            TestContext.Current.CancellationToken);

        var reloaded = await new VpnPolicyStore(
            time,
            NullLogger<VpnPolicyStore>.Instance,
            path).GetAsync(TestContext.Current.CancellationToken);

        reloaded.MinLoad.ShouldBe(80);
        reloaded.MaxLoad.ShouldBe(80);
        reloaded.Countries.ShouldBe(["JP", "US"]);
        reloaded.Servers["JP#10"].Status.ShouldBe(VpnServerStatuses.QuotaExhausted);
        reloaded.Servers["JP#10"].FailureCount.ShouldBe(1);
        Directory.GetFiles(directory, "*.tmp-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task Recovery_settings_persist_and_webhook_secret_is_not_written_to_disk()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "vpn-policy.json");
        var time = new FakeTimeProvider();
        var secrets = new InMemorySecretStore();
        var store = new VpnPolicyStore(
            time,
            NullLogger<VpnPolicyStore>.Instance,
            path,
            recoveryDefaults: new VpnRecoverySettings { Enabled = false },
            secrets: secrets);

        var view = await store.SaveRecoveryAsync(
            new VpnRecoveryUpdatePayload(
                Enabled: true,
                ContinuationPrompt: "resume from checkpoint",
                HandleQuotaExceeded: false,
                CooldownSeconds: 90,
                MaxAttemptsPerSession: 5,
                StallThresholdSeconds: 120,
                Country: " jp ",
                City: "Tokyo",
                DiscordEnabled: true,
                DiscordWebhookUrl: "https://discord.com/api/webhooks/42/token"),
            TestContext.Current.CancellationToken);

        view.Enabled.ShouldBeTrue();
        view.HandleQuotaExceeded.ShouldBeFalse();
        view.CooldownSeconds.ShouldBe(90);
        view.MaxAttemptsPerSession.ShouldBe(5);
        view.StallThresholdSeconds.ShouldBe(120);
        view.Country.ShouldBe("JP");
        view.City.ShouldBe("Tokyo");
        view.DiscordWebhookConfigured.ShouldBeTrue();

        var file = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        file.ShouldNotContain("discord.com");
        file.ShouldNotContain("token");

        var reloaded = new VpnPolicyStore(
            time,
            NullLogger<VpnPolicyStore>.Instance,
            path,
            recoveryDefaults: new VpnRecoverySettings { Enabled = false },
            secrets: secrets);
        var recovery = await reloaded.GetRecoveryAsync(TestContext.Current.CancellationToken);
        recovery.Enabled.ShouldBeTrue();
        recovery.ContinuationPrompt.ShouldBe("resume from checkpoint");
        recovery.CooldownSeconds.ShouldBe(90);

        var cleared = await reloaded.SaveRecoveryAsync(
            new VpnRecoveryUpdatePayload(
                Country: string.Empty,
                City: string.Empty,
                DiscordWebhookUrl: string.Empty),
            TestContext.Current.CancellationToken);
        cleared.DiscordWebhookConfigured.ShouldBeFalse();
        cleared.Country.ShouldBeNull();
        cleared.City.ShouldBeNull();
        (await reloaded.GetDiscordWebhookAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Recovery_rejects_a_non_discord_webhook()
    {
        var directory = TempDirectory();
        var store = new VpnPolicyStore(
            new FakeTimeProvider(),
            NullLogger<VpnPolicyStore>.Instance,
            Path.Combine(directory, "vpn-policy.json"),
            secrets: new InMemorySecretStore());

        var error = await Should.ThrowAsync<VpnException>(() => store.SaveRecoveryAsync(
            new VpnRecoveryUpdatePayload(DiscordWebhookUrl: "https://example.com/api/webhooks/1/token"),
            TestContext.Current.CancellationToken));

        error.Code.ShouldBe("vpn_recovery_invalid_webhook");
    }

    [Fact]
    public void Recovery_settings_clamp_and_normalize_input()
    {
        var settings = new VpnRecoverySettings
        {
            ContinuationPrompt = "   ",
            Country = " jp ",
            City = "   ",
            Servers = ["JP#1", " jp#1 ", ""],
            CooldownSeconds = -5,
            MaxAttemptsPerSession = 99,
            StallThresholdSeconds = 999_999,
        }.Normalize();

        settings.ContinuationPrompt.ShouldBe(VpnRecoverySettings.DefaultContinuationPrompt);
        settings.Country.ShouldBe("JP");
        settings.City.ShouldBeNull();
        settings.Servers.ShouldBe(["JP#1"]);
        settings.CooldownSeconds.ShouldBe(0);
        settings.MaxAttemptsPerSession.ShouldBe(10);
        settings.StallThresholdSeconds.ShouldBe(86_400);
        settings.ShouldHandle(ErrorClass.QuotaExceeded).ShouldBeTrue();
        settings.ShouldHandle(ErrorClass.Aborted).ShouldBeFalse();
        settings.ShouldHandle(ErrorClass.ToolError).ShouldBeFalse();
        settings.ShouldHandle(ErrorClass.Unknown, stalled: true).ShouldBeTrue();
    }

    [Fact]
    public void Selector_prefers_low_load_then_high_score_and_skips_current()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var policy = (VpnPolicyDocument.CreateDefault(now) with
        {
            ExcludeUsedWithinHours = 24,
            Servers = new Dictionary<string, VpnServerPolicyState>
            {
                ["JP#1"] = new(VpnServerStatuses.Ok, "JP", "Tokyo", LastUsedAt: now.AddHours(-1)),
                ["JP#4"] = new(VpnServerStatuses.Ok, "JP", "Tokyo", LastUsedAt: now.AddDays(-2)),
            },
        }).Normalize();
        var servers = new[]
        {
            Server("JP#1", load: 10, score: 99),
            Server("JP#2", load: 20, score: 50),
            Server("JP#3", load: 20, score: 80),
            Server("JP#4", load: 15, score: 1),
        };

        var result = VpnServerSelector.Select(
            policy,
            servers,
            currentServer: "JP#2",
            avoid: null,
            now,
            random: () => 0.5);

        result.Selected!.Name.ShouldBe("JP#4");
        result.Candidates.Single(candidate => candidate.Server.Name == "JP#1").Reason
            .ShouldBe(VpnSelectionReasons.RecentlyUsed);
        result.Candidates.Single(candidate => candidate.Server.Name == "JP#2").Reason
            .ShouldBe(VpnSelectionReasons.Current);
    }

    [Fact]
    public void Selector_reports_earliest_available_when_every_server_is_cooling_down()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var policy = (VpnPolicyDocument.CreateDefault(now) with
        {
            Servers = new Dictionary<string, VpnServerPolicyState>
            {
                ["JP#1"] = new(VpnServerStatuses.QuotaExhausted, "JP", "Tokyo", AvailableAt: now.AddMinutes(20)),
                ["JP#2"] = new(VpnServerStatuses.QuotaExhausted, "JP", "Tokyo", AvailableAt: now.AddMinutes(10)),
            },
        }).Normalize();

        var result = VpnServerSelector.Select(
            policy,
            new[] { Server("JP#1", 10, 10), Server("JP#2", 20, 20) },
            currentServer: null,
            avoid: null,
            now);

        result.Selected.ShouldBeNull();
        result.Reason.ShouldBe(VpnSelectionReasons.NoCandidates);
        result.NextAvailableAt.ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public void Selector_retries_a_failed_server_after_its_cooldown_expires()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var policy = (VpnPolicyDocument.CreateDefault(now) with
        {
            Servers = new Dictionary<string, VpnServerPolicyState>
            {
                ["JP#1"] = new(
                    VpnServerStatuses.QuotaExhausted,
                    "JP",
                    "Tokyo",
                    AvailableAt: now.AddMinutes(-1)),
            },
        }).Normalize();

        var result = VpnServerSelector.Select(
            policy,
            new[] { Server("JP#1", 10, 10) },
            currentServer: null,
            avoid: null,
            now);

        result.Selected!.Name.ShouldBe("JP#1");
        result.Reason.ShouldBe(VpnSelectionReasons.Selected);
    }

    [Fact]
    public void Failure_parser_reads_korean_and_english_retry_delays()
    {
        VpnFailureParser.ParseRetryAfter("Free usage exceeded. \uC7AC\uC2DC\uB3C4 \uC911 7091\uCD08 \uD6C4")
            .ShouldBe(TimeSpan.FromSeconds(7091));
        VpnFailureParser.ParseRetryAfter("retrying in 42 seconds")
            .ShouldBe(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Failure_parser_prefers_next_at_and_classifies_quota_and_timeout()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var quota = VpnFailureParser.Assess(
            ErrorClass.QuotaExceeded,
            "Free usage exceeded",
            new RetryInfo(1, "\uC7AC\uC2DC\uB3C4 \uC911 30\uCD08 \uD6C4", now.AddMinutes(2)),
            VpnRecoveryNoticeKind.QuotaExceeded,
            now);
        quota.ServerStatus.ShouldBe(VpnServerStatuses.QuotaExhausted);
        quota.RetryAfter.ShouldBe(TimeSpan.FromMinutes(2));

        var timeout = VpnFailureParser.Assess(
            ErrorClass.Network,
            "fetch failed",
            null,
            VpnRecoveryNoticeKind.Stalled,
            now);
        timeout.ServerStatus.ShouldBe(VpnServerStatuses.OpenCodeTimeout);
        timeout.RetryAfter.ShouldBeNull();
    }

    private static VpnServer Server(string name, int load, double score) =>
        new(name, "JP", "Japan", "Tokyo", 2, "Plus", load, score, Array.Empty<string>());

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai-ctrl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public ValueTask<string?> LoadAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_values.GetValueOrDefault(key));
        }

        public ValueTask SaveAsync(string key, string value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _values[key] = value;
            return ValueTask.CompletedTask;
        }
    }
}
