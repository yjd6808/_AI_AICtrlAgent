using System.Text.Json;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Vpn;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class VpnSystemTests
{
    [Fact]
    public void Status_parser_reads_tunnel_fields()
    {
        var status = ProtonVpnCli.ParseStatus(
            """
            {
              "signedIn": true,
              "username": "user@example.com",
              "expiresAt": "2026-02-01T00:00:00Z",
              "tunnel": {
                "up": true,
                "server": "JP#12",
                "country": "JP",
                "city": "Tokyo",
                "detail": "connected"
              }
            }
            """);

        status.SignedIn.ShouldBeTrue();
        status.Username.ShouldBe("user@example.com");
        status.ExpiresAt.ShouldBe(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        status.TunnelUp.ShouldBeTrue();
        status.TunnelServer.ShouldBe("JP#12");
        status.TunnelCity.ShouldBe("Tokyo");
    }

    [Fact]
    public void Status_parser_reads_proton_app_fields()
    {
        var status = ProtonVpnCli.ParseStatus(
            """
            {
              "ok": true,
              "connected": true,
              "country": "네덜란드",
              "server": "암스테르담 - NL#853",
              "ip": "205.147.16.140",
              "protocol": "WireGuard (UDP)",
              "detail": "암스테르담 - NL#853"
            }
            """);

        status.SignedIn.ShouldBeTrue();
        status.TunnelUp.ShouldBeTrue();
        status.TunnelServer.ShouldBe("NL#853");
        status.TunnelCountry.ShouldBe("NL");
        status.TunnelCity.ShouldBe("암스테르담");
        status.ExitIp.ShouldBe("205.147.16.140");
    }

    [Fact]
    public void Server_parser_reads_proton_app_tier_and_country()
    {
        var servers = ProtonVpnCli.ParseServers(
            """
            {
              "ok": true,
              "country": { "code": "JP", "name": "Japan" },
              "servers": [
                { "name": "JP#213", "country": "JP", "city": "Osaka", "load": 18, "score": 1.4, "tier": "Plus" }
              ]
            }
            """);

        var server = servers.ShouldHaveSingleItem();
        server.CountryName.ShouldBe("Japan");
        server.Tier.ShouldBe(2);
        server.TierName.ShouldBe("Plus");
        server.City.ShouldBe("Osaka");
    }

    [Fact]
    public void Connect_parser_reads_proton_app_status_shape()
    {
        var result = ProtonVpnCli.ParseConnect(
            """
            {
              "ok": true,
              "connected": true,
              "country": "일본",
              "server": "도쿄도 - JP#277",
              "ip": "159.26.119.36",
              "protocol": "WireGuard (UDP)",
              "detail": "도쿄도 - JP#277"
            }
            """);

        result.Server.ShouldBe("JP#277");
        result.Country.ShouldBe("JP");
        result.CountryName.ShouldBe("일본");
        result.City.ShouldBe("도쿄도");
        result.ExitIp.ShouldBe("159.26.119.36");
    }

    [Fact]
    public async Task Proton_app_uses_timeout_and_positional_server_arguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var cli = new ScriptedCli(args => args switch
        {
            "connect NL#900 --json --timeout 30" => CliResult(
                """{"ok":true,"connected":true,"server":"NL#900","ip":"203.0.113.9"}"""),
            "disconnect --json --timeout 30" => CliResult("""{"ok":true}"""),
            "servers NL --json --limit 2000" => CliResult(
                """{"ok":true,"country":{"code":"NL","name":"Netherlands"},"servers":[{"name":"NL#900","country":"NL","city":"Amsterdam","load":1,"score":0.1,"tier":"Plus"}]}"""),
            _ => throw new InvalidOperationException(args),
        });
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var auto = new ProtonVpnCli(cli, time, "protonvpn-auto.exe");

        var result = await auto.ConnectAsync("NL#900", TimeSpan.FromSeconds(30), ct);
        var servers = await auto.GetServersAsync("NL", TimeSpan.FromSeconds(30), ct);
        await auto.DisconnectAsync(TimeSpan.FromSeconds(30), ct);

        result.Server.ShouldBe("NL#900");
        servers.ShouldHaveSingleItem().Name.ShouldBe("NL#900");
        cli.Calls.ShouldBe(new[]
        {
            "connect NL#900 --json --timeout 30",
            "servers NL --json --limit 2000",
            "disconnect --json --timeout 30",
        });
    }

    [Fact]
    public void Fallback_options_read_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["vpn:fallback:mode"] = "ServerLink",
                ["vpn:fallback:readyUrl"] = "http://127.0.0.1:9999/ready",
                ["vpn:fallback:minutes"] = "2",
            })
            .Build();

        var options = VpnOptions.FromConfiguration(configuration);

        options.Fallback.Mode.ShouldBe(VpnFallbackMode.ServerLink);
        options.Fallback.ReadyUri.ShouldBe(new Uri("http://127.0.0.1:9999/ready"));
        options.Fallback.Timeout.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task Status_is_published_to_outbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: true)),
                _ => throw new InvalidOperationException(args),
            });

        var status = await harness.System.GetStatusAsync(ct);

        status.CliFound.ShouldBeTrue();
        status.SignedIn.ShouldBeTrue();
        status.TunnelUp.ShouldBeTrue();
        status.Server.ShouldBe("JP#1");
        status.ExitIp.ShouldBeNull();

        var record = (await harness.Outbox.GetUnackedAsync(10, ct)).ShouldHaveSingleItem();
        record.Type.ShouldBe("vpn.state");
        var published = JsonSerializer.Deserialize(record.PayloadJson, AcJsonContext.Default.VpnStatusView);
        published.ShouldNotBeNull();
        published.Server.ShouldBe("JP#1");
    }

    [Fact]
    public async Task Connect_selects_lowest_load_server_and_records_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var statusRequests = 0;
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(
                    tunnel: statusRequests++ > 0,
                    server: "JP#2",
                    city: "Tokyo")),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            });

        var result = await harness.System.ConnectAsync(
            new VpnConnectPayload(null, "JP", "Tokyo"),
            "cmd_vpn",
            ct);

        result.Connect.ShouldNotBeNull();
        result.Connect.Server.ShouldBe("JP#2");
        result.Connect.ExitIp.ShouldBe("203.0.113.8");
        result.Status.TunnelUp.ShouldBeTrue();
        harness.History.GetLastConnected("JP#2").ShouldNotBeNull();
        harness.Cli.Calls.ShouldContain("connect JP#2 --json --yes");
    }

    [Fact]
    public async Task Proton_app_connect_allows_running_gui()
    {
        var ct = TestContext.Current.CancellationToken;
        var statusRequests = 0;
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(statusRequests++ == 0
                    ? """{"ok":true,"connected":false}"""
                    : """{"ok":true,"connected":true,"server":"NL#900","ip":"203.0.113.9"}"""),
                "countries --json" => CliResult(
                    """{"ok":true,"countries":[{"code":"NL","name":"Netherlands","count":1,"cities":["Amsterdam"]}]}"""),
                "servers NL --json --limit 2000" => CliResult(
                    """{"ok":true,"country":{"code":"NL","name":"Netherlands"},"servers":[{"name":"NL#900","country":"NL","city":"Amsterdam","load":1,"score":0.1,"tier":"Plus"}]}"""),
                "connect NL#900 --json --timeout 90" => CliResult(
                    """{"ok":true,"connected":true,"server":"NL#900","country":"Netherlands","ip":"203.0.113.9"}"""),
                _ => throw new InvalidOperationException(args),
            },
            cliPath: "protonvpn-auto.exe",
            protonGuiRunning: () => true);

        var result = await harness.System.ConnectAsync(
            new VpnConnectPayload("NL#900", "NL", null),
            "cmd_vpn_auto",
            ct);

        result.Connect.ShouldNotBeNull();
        result.Connect.Server.ShouldBe("NL#900");
        result.Status.TunnelUp.ShouldBeTrue();
        harness.Cli.Calls.ShouldContain("connect NL#900 --json --timeout 90");
    }

    [Fact]
    public async Task Explicit_connect_refreshes_servers_even_when_cache_is_fresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverRequests = 0;
        ProcessResult Servers()
        {
            serverRequests++;
            return CliResult(ServersJson);
        }

        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => Servers(),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            });

        await harness.System.ConnectAsync(
            new VpnConnectPayload("JP#2", "JP", null),
            "cmd_refresh_1",
            ct);
        await harness.System.ConnectAsync(
            new VpnConnectPayload("JP#2", "JP", null),
            "cmd_refresh_2",
            ct);

        serverRequests.ShouldBe(4);
    }

    [Fact]
    public async Task Connect_reports_switching_and_waiting_tunnel_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var steps = new List<string>();
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            });

        await harness.System.ConnectAsync(
            new VpnConnectPayload("JP#2", "JP", null),
            "cmd_progress",
            ct,
            progress =>
            {
                steps.Add(progress.Step);
                return Task.CompletedTask;
            });

        steps.ShouldBe(new[] { "switching", "waiting_tunnel" });
    }

    [Fact]
    public async Task Connect_rejects_server_missing_from_recent_list()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                _ => throw new InvalidOperationException(args),
            });

        var exception = await Should.ThrowAsync<VpnException>(async () =>
            await harness.System.ConnectAsync(
                new VpnConnectPayload("US#9", "JP", null),
                "cmd_unknown",
                ct));

        exception.Code.ShouldBe(AcErrorCodes.InvalidPayload);
        harness.Cli.Calls.ShouldNotContain(call => call.StartsWith("connect ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connect_fails_safe_when_tunnel_never_becomes_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new StubReadinessProbe(_ => false);
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: true)),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                "disconnect --json --yes" => CliResult("{}"),
                _ => throw new InvalidOperationException(args),
            },
            fallback: new VpnFallbackOptions
            {
                Timeout = TimeSpan.FromMilliseconds(300),
            },
            readinessProbe: probe,
            clock: TimeProvider.System);

        var exception = await Should.ThrowAsync<VpnException>(async () =>
            await harness.System.ConnectAsync(
                new VpnConnectPayload("JP#2", "JP", null),
                "cmd_fallback",
                ct));

        exception.Code.ShouldBe(AcErrorCodes.InternalError);
        exception.Message.ShouldContain("disconnected");
        harness.Cli.Calls.ShouldContain("disconnect --json --yes");
        probe.Calls.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Cancelling_during_fallback_disconnects_tunnel()
    {
        var ct = TestContext.Current.CancellationToken;
        var probeCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new StubReadinessProbe(_ =>
        {
            probeCalled.TrySetResult();
            return false;
        });
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                "disconnect --json --yes" => CliResult("{}"),
                _ => throw new InvalidOperationException(args),
            },
            fallback: new VpnFallbackOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
            },
            readinessProbe: probe,
            clock: TimeProvider.System);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var connect = harness.System.ConnectAsync(
            new VpnConnectPayload("JP#2", "JP", null),
            "cmd_cancel", cancel.Token);

        await probeCalled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await connect);
        harness.Cli.Calls.ShouldContain("disconnect --json --yes");
    }

    [Fact]
    public async Task Connect_succeeds_after_tunnel_becomes_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new StubReadinessProbe(call => call >= 2);
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            },
            fallback: new VpnFallbackOptions
            {
                Timeout = TimeSpan.FromSeconds(3),
            },
            readinessProbe: probe,
            clock: TimeProvider.System);

        var result = await harness.System.ConnectAsync(
            new VpnConnectPayload("JP#2", "JP", null),
            "cmd_ready",
            ct);

        result.Connect.ShouldNotBeNull();
        probe.Calls.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Connect_uses_server_link_fallback_without_probing_tunnel()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new StubReadinessProbe(_ => false);
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            },
            fallback: new VpnFallbackOptions
            {
                Mode = VpnFallbackMode.ServerLink,
                Timeout = TimeSpan.FromSeconds(2),
            },
            readinessProbe: probe,
            serverLinkConnected: () => true);

        var result = await harness.System.ConnectAsync(
            new VpnConnectPayload("JP#2", "JP", null),
            "cmd_link",
            ct);

        result.Connect.ShouldNotBeNull();
        probe.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Connect_rejects_second_operation_while_busy()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(tunnel: false)),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#1 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            });
        harness.Cli.ConnectGate = gate;

        var first = harness.System.ConnectAsync(
            new VpnConnectPayload("JP#1", null, null),
            "cmd_first",
            ct);
        await harness.Cli.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var exception = await Should.ThrowAsync<VpnException>(async () =>
            await harness.System.ConnectAsync(
                new VpnConnectPayload("JP#1", null, null),
                "cmd_second",
                ct));
        exception.Code.ShouldBe(AcErrorCodes.VpnBusy);

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Connect_requires_signin_and_does_not_start_cli()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult("""{"signedIn":false}"""),
                _ => throw new InvalidOperationException(args),
            });

        var exception = await Should.ThrowAsync<VpnException>(async () =>
            await harness.System.ConnectAsync(
                new VpnConnectPayload("JP#1", null, null),
                "cmd_signin",
                ct));

        exception.Code.ShouldBe(AcErrorCodes.VpnSigninRequired);
        harness.Cli.Calls.ShouldNotContain(call => call.StartsWith("connect ", StringComparison.Ordinal));
    }

    [Fact]
    public void Server_page_orders_connected_and_suggested_city()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var history = new ConnectionHistory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.json"), time);
        history.Record("JP#2", time.GetUtcNow().AddHours(-1));
        var country = new VpnCountry("JP", "Japan", 2, new[] { "Tokyo", "Osaka" });
        var servers = new[]
        {
            new VpnServer("JP#1", "JP", "Japan", "Osaka", 2, "Plus", 10, 1, Array.Empty<string>()),
            new VpnServer("JP#2", "JP", "Japan", "Tokyo", 2, "Plus", 50, 1, Array.Empty<string>()),
        };
        var status = Status(tunnel: true, server: "JP#2", country: "JP", city: "Tokyo");

        var page = VpnServerListBuilder.Build(country, servers, status, history, time.GetUtcNow(), null);

        page.SuggestedCity.ShouldBe("Tokyo");
        page.Cities[0].City.ShouldBe("Tokyo");
        page.Cities[0].Highlight.ShouldBeTrue();
        page.Cities[0].Servers[0].RowState.ShouldBe(VpnRowState.Connected);
    }

    [Fact]
    public async Task Catalog_loads_all_countries_and_caches_until_refreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverRequests = 0;
        ProcessResult Servers()
        {
            serverRequests++;
            return CliResult(ServersJson);
        }

        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => Servers(),
                _ => throw new InvalidOperationException(args),
            });

        var first = await harness.System.GetServerCatalogAsync(refresh: false, ct);
        var second = await harness.System.GetServerCatalogAsync(refresh: false, ct);
        var refreshed = await harness.System.GetServerCatalogAsync(refresh: true, ct);

        first.Select(server => server.Name).ShouldBe(new[] { "JP#1", "JP#2" });
        second.Select(server => server.Name).ShouldBe(first.Select(server => server.Name));
        refreshed.Count.ShouldBe(2);
        serverRequests.ShouldBe(2);
    }

    [Fact]
    public async Task Connect_records_policy_usage_and_exposes_current_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var statusRequests = 0;
        await using var harness = await VpnHarness.StartAsync(
            args => args switch
            {
                "status --json" => CliResult(StatusJson(
                    tunnel: statusRequests++ > 0,
                    server: "JP#2",
                    city: "Tokyo")),
                "countries --json" => CliResult(CountriesJson),
                "servers --country JP --json --limit 2000" => CliResult(ServersJson),
                "connect JP#2 --json --yes" => CliResult(ConnectJson),
                _ => throw new InvalidOperationException(args),
            });

        await harness.System.ConnectAsync(
            new VpnConnectPayload(null, "JP", "Tokyo"),
            "cmd_policy",
            ct);

        var document = await harness.Policy.GetAsync(ct);
        var state = document.Servers["JP#2"];
        state.Status.ShouldBe(VpnServerStatuses.Ok);
        state.Country.ShouldBe("JP");
        state.City.ShouldBe("Tokyo");
        state.LastExitIp.ShouldBe("203.0.113.8");
        state.FailureCount.ShouldBe(0);
        state.LastUsedAt.ShouldNotBeNull();

        var policy = await harness.System.GetPolicyAsync(ct);
        policy.CurrentServer.ShouldBe("JP#2");
    }

    private static VpnStatusView Status(bool tunnel, string server, string country, string city) =>
        new(
            true,
            "protonvpn.exe",
            false,
            false,
            true,
            "user",
            null,
            tunnel,
            tunnel ? server : null,
            tunnel ? country : null,
            tunnel ? city : null,
            null,
            null,
            "ok",
            null,
            null,
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

    private static ProcessResult CliResult(string stdout) => new(0, stdout, string.Empty);

    private static string StatusJson(bool tunnel, string server = "JP#1", string city = "Tokyo") =>
        $$"""
        {
          "signedIn": true,
          "username": "user@example.com",
          "tunnel": {
            "up": {{(tunnel ? "true" : "false")}},
            "server": "{{server}}",
            "country": "JP",
            "city": "{{city}}"
          }
        }
        """;

    private const string CountriesJson =
        """
        {"countries":[{"code":"JP","name":"Japan","count":2,"cities":["Tokyo","Osaka"]}]}
        """;

    private const string ServersJson =
        """
        {"servers":[
          {"name":"JP#1","country":"JP","countryName":"Japan","city":"Tokyo","tier":2,"tierName":"Plus","load":20,"score":5,"features":["p2p"]},
          {"name":"JP#2","country":"JP","countryName":"Japan","city":"Tokyo","tier":2,"tierName":"Plus","load":10,"score":3,"features":["p2p"]}
        ]}
        """;

    private const string ConnectJson =
        """
        {"connected":{"server":"JP#2","country":"JP","countryName":"Japan","city":"Tokyo","load":10,"exitIp":"203.0.113.8","connectedAt":"2026-01-02T00:00:00Z"}}
        """;

    private sealed class VpnHarness : IAsyncDisposable
    {
        private readonly string _directory;

        private VpnHarness(
            string directory,
            ClientDb db,
            OutboxStore outbox,
            ScriptedCli cli,
            ConnectionHistory history,
            VpnPolicyStore policy,
            VpnSystem system)
        {
            _directory = directory;
            Db = db;
            Outbox = outbox;
            Cli = cli;
            History = history;
            Policy = policy;
            System = system;
        }

        public ClientDb Db { get; }

        public OutboxStore Outbox { get; }

        public ScriptedCli Cli { get; }

        public ConnectionHistory History { get; }

        public VpnPolicyStore Policy { get; }

        public VpnSystem System { get; }

        public static async Task<VpnHarness> StartAsync(
            Func<string, ProcessResult> handler,
            VpnFallbackOptions? fallback = null,
            IVpnReadinessProbe? readinessProbe = null,
            Func<bool>? serverLinkConnected = null,
            TimeProvider? clock = null,
            string cliPath = "protonvpn.exe",
            Func<bool>? protonGuiRunning = null)
        {
            var ct = TestContext.Current.CancellationToken;
            var directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));
            var time = clock ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
            var db = new ClientDb(Path.Combine(directory, "client.db"), time);
            await db.InitializeAsync(ct);
            var outbox = new OutboxStore(db, time);
            var cli = new ScriptedCli(handler);
            var history = new ConnectionHistory(Path.Combine(directory, "vpn-history.json"), time);
            var policy = new VpnPolicyStore(
                time,
                NullLogger<VpnPolicyStore>.Instance,
                Path.Combine(directory, "vpn-policy.json"));
            var options = new VpnOptions
            {
                CliPath = cliPath,
                HistoryPath = Path.Combine(directory, "vpn-history.json"),
                Fallback = fallback ?? new VpnFallbackOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(200),
                },
            };
            var system = new VpnSystem(
                options,
                outbox,
                time,
                NullLogger<VpnSystem>.Instance,
                cli: new ProtonVpnCli(cli, time, cliPath),
                history: history,
                protonGuiRunning: protonGuiRunning ?? (() => false),
                readinessProbe: readinessProbe ?? new StubReadinessProbe(_ => true),
                serverLinkConnected: serverLinkConnected,
                policyStore: policy);
            return new VpnHarness(directory, db, outbox, cli, history, policy, system);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubReadinessProbe : IVpnReadinessProbe
    {
        private readonly Func<int, bool> _result;

        private int _calls;

        public StubReadinessProbe(Func<int, bool> result)
        {
            _result = result;
        }

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> IsReadyAsync(Uri uri, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(_result(call));
        }
    }

    private sealed class ScriptedCli : IProcessRunner
    {
        private readonly Func<string, ProcessResult> _handler;

        private readonly object _gate = new();

        private readonly List<string> _calls = new();

        public ScriptedCli(Func<string, ProcessResult> handler)
        {
            _handler = handler;
        }

        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? ConnectGate { get; set; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _calls.Add(arguments);
            }

            if (arguments.StartsWith("connect ", StringComparison.Ordinal))
            {
                ConnectStarted.TrySetResult();
                if (ConnectGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
            }

            return _handler(arguments);
        }
    }
}
