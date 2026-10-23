using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Requests;
using AiCtrl.Server.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class RequestPipelineTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly HttpClient _browser = new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    });

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private TestServerHost _host = null!;

    private MachineId _machineId;

    private TestAgent _agent = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _host = await TestServerHost.StartAsync(_directory);
        _browser.BaseAddress = new Uri(_host.BaseUrl);
        using var login = await _browser.PostAsync("/test-login?email=test%40example.com&name=Test+User", null, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var enrollment = _host.Services.GetRequiredService<EnrollmentService>();
        var code = await enrollment.CreateCodeAsync(ct);
        var enrolled = await enrollment.EnrollAsync(
            new EnrollmentRequest(
                code.Code,
                Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
                "test-host",
                "test-os",
                "1.0.0"),
            "test",
            ct);
        enrolled.Success.ShouldBeTrue($"enroll failed: {enrolled.ErrorCode}");
        _machineId = new MachineId(enrolled.MachineId!);

        var auth = _host.Services.GetRequiredService<AgentAuthService>();
        var challenge = await auth.ChallengeAsync(_machineId, ct);
        var token = await auth.IssueTokenAsync(
            new AgentTokenRequest(
                _machineId,
                challenge.Value!,
                Convert.ToBase64String(_key.SignData(
                    Encoding.UTF8.GetBytes($"aictrl-agent-v1|{_machineId.Value}|{challenge.Value}"),
                    HashAlgorithmName.SHA256))),
            ct);
        token.Success.ShouldBeTrue();

        _agent = await TestAgent.ConnectAsync(_host.BaseUrl, _machineId, token.Value!, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }

        _browser.Dispose();
        _key.Dispose();
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Live_query_round_trips_and_sets_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        _agent.Responder = packet =>
        {
            var data = JsonDocument.Parse(
                """[{"id":{"value":"opencode:test"},"kind":"OpenCode","endpoint":"local","version":"1","mode":"Managed","healthy":true,"managed":true,"capabilities":{}}]""")
                .RootElement.Clone();
            return Task.FromResult<ResponsePacket?>(Response(packet, data));
        };

        using var response = await _browser.GetAsync(
            $"/api/v1/machines/{_machineId.Value}/instances",
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Trace-Id").ShouldNotBeEmpty();
        var body = await response.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseJsonElement, ct);

        body.ShouldNotBeNull();
        body.Meta.Source.ShouldBe(MetaSources.Live);
        body.Meta.MachineOnline.ShouldBeTrue();
        body.Meta.Stale.ShouldBeFalse();
        body.Data.GetArrayLength().ShouldBe(1);
        _agent.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Files_list_proxy_returns_entries_from_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        _agent.Responder = packet => Task.FromResult<ResponsePacket?>(Response(
            packet,
            JsonDocument.Parse(
                """{"entries":[{"name":"note.txt","relPath":"note.txt","isDir":false,"size":4,"modified":"2026-01-01T00:00:00Z","hidden":false,"denied":false,"isReparsePoint":false}],"nextCursor":null,"hasMore":false}""")
                .RootElement.Clone()));

        using var response = await _browser.GetAsync(
            $"/api/v1/machines/{_machineId.Value}/workspaces/ws_test/files?relPath=&limit=50",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task File_stat_alias_accepts_path_query()
    {
        var ct = TestContext.Current.CancellationToken;
        string? queriedPath = null;
        _agent.Responder = packet =>
        {
            queriedPath = packet.Payload.GetProperty("path").GetString();
            return Task.FromResult<ResponsePacket?>(Response(
                packet,
                JsonDocument.Parse(
                    """{"name":"note.txt","relPath":"docs/note.txt","isDir":false,"size":4,"modified":"2026-01-01T00:00:00Z","hidden":false,"denied":false,"isReparsePoint":false,"sha256":null}""")
                    .RootElement.Clone()));
        };

        using var response = await _browser.GetAsync(
            $"/api/v1/machines/{_machineId.Value}/workspaces/ws_test/stat?path=docs/note.txt",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        queriedPath.ShouldBe("docs/note.txt");
    }

    [Fact]
    public async Task Workspace_register_dispatches_command_to_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        string? dispatchedType = null;
        _agent.Responder = packet =>
        {
            dispatchedType = packet.Type;
            return Task.FromResult<ResponsePacket?>(Response(
                packet,
                JsonDocument.Parse(
                    """{"workspaceId":"ws_0123abcd","name":"demo","root":"C:\\Work\\demo","allowUpload":true,"allowDownload":true,"allowMkdir":true,"allowDelete":false,"denyPatterns":[],"linkedSessions":[]}""")
                    .RootElement.Clone()));
        };

        using var response = await _browser.PostAsJsonAsync(
            $"/api/v1/machines/{_machineId.Value}/workspaces",
            new { path = @"C:\Work\demo" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("data").GetProperty("result").GetProperty("workspaceId")
            .GetString().ShouldBe("ws_0123abcd");
        dispatchedType.ShouldBe(CommandTypes.WorkspaceRegister);
    }

    [Fact]
    public async Task Workspace_register_with_stale_auth_requires_reauth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var login = await _browser.PostAsync("/test-login?authTime=1", null, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var response = await _browser.PostAsJsonAsync(
            $"/api/v1/machines/{_machineId.Value}/workspaces",
            new { path = @"C:\Work\demo" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, body);
        body.ShouldContain(AcErrorCodes.ReauthRequired);
        _agent.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_identical_queries_share_one_live_dispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agent.Responder = async packet =>
        {
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return Response(packet, JsonDocument.Parse("""{"ok":true}""").RootElement.Clone());
        };
        var service = _host.Services.GetRequiredService<RemoteQueryService>();
        var payload = JsonDocument.Parse("{}").RootElement.Clone();
        var first = service.QueryAsync(
            _machineId.Value,
            QueryTypes.AgentInstances,
            payload,
            "agent.instances:{}",
            "test",
            "trace-1",
            ct);
        await WaitForRequestCountAsync(1, ct);
        var second = service.QueryAsync(
            _machineId.Value,
            QueryTypes.AgentInstances,
            payload,
            "agent.instances:{}",
            "test",
            "trace-2",
            ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        release.SetResult();

        var results = await Task.WhenAll(first, second);
        results.ShouldAllBe(result => result.Success);
        _agent.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_key_returns_the_same_command_without_redispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        _agent.Responder = packet => Task.FromResult<ResponsePacket?>(
            Response(packet, JsonDocument.Parse("""{"ok":true}""").RootElement.Clone()));
        var url = $"/api/v1/machines/{_machineId.Value}/sessions/ses_1/prompt?instance=opencode%3Atest&wait=5";
        var body = new PromptBody("hello", null, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, AcJsonContext.Default.PromptBody),
        };
        request.Headers.Add("Idempotency-Key", "idem-1");

        using var first = await _browser.SendAsync(request, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseCommandDetailView, ct);
        var commandId = firstBody!.Data.CommandId;

        using var second = await _browser.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, AcJsonContext.Default.PromptBody),
                Headers = { { "Idempotency-Key", "idem-1" } },
            },
            ct);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseCommandDetailView, ct);

        secondBody!.Data.CommandId.ShouldBe(commandId);
        _agent.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Offline_query_uses_stale_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        _agent.Responder = packet => Task.FromResult<ResponsePacket?>(
            Response(packet, JsonDocument.Parse("""{"value":42}""").RootElement.Clone()));
        var url = $"/api/v1/machines/{_machineId.Value}/instances?kind=OpenCode";

        using var live = await _browser.GetAsync(url, ct);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        var liveBody = await live.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseJsonElement, ct);
        liveBody!.Meta.Source.ShouldBe(MetaSources.Live);

        await _agent.DisposeAsync();
        _agent = null!;
        await WaitForDisconnectAsync(ct);

        using var cached = await _browser.GetAsync(url, ct);
        cached.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cachedBody = await cached.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseJsonElement, ct);
        cachedBody!.Meta.Source.ShouldBe(MetaSources.Cache);
        cachedBody.Meta.Stale.ShouldBeTrue();
        cachedBody.Data.GetProperty("value").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task Command_without_response_returns_pending_and_is_queryable()
    {
        var ct = TestContext.Current.CancellationToken;
        _agent.Responder = _ => Task.FromResult<ResponsePacket?>(null);
        var url = $"/api/v1/machines/{_machineId.Value}/sessions/ses_1/abort?instance=opencode%3Atest&wait=0.2";

        using var response = await _browser.PostAsync(url, null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var pending = await response.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseCommandPendingView, ct);
        pending.ShouldNotBeNull();
        pending.Data.RequestId.ShouldNotBeNullOrWhiteSpace();

        using var detailResponse = await _browser.GetAsync($"/api/v1/commands/{pending.Data.RequestId}", ct);
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseCommandDetailView, ct);
        detail!.Data.Status.ShouldBe(CommandStatus.Acknowledged);
    }

    [Fact]
    public async Task Command_issue_is_written_to_audit_log()
    {
        var ct = TestContext.Current.CancellationToken;
        _agent.Responder = packet => Task.FromResult<ResponsePacket?>(
            Response(packet, JsonDocument.Parse("""{"ok":true}""").RootElement.Clone()));
        using var command = await _browser.PostAsync(
            $"/api/v1/machines/{_machineId.Value}/sessions/ses_1/abort?instance=opencode%3Atest&wait=5",
            null,
            ct);
        var commandBody = await command.Content.ReadAsStringAsync(ct);
        command.StatusCode.ShouldBe(HttpStatusCode.OK, commandBody);

        using var response = await _browser.GetAsync("/api/v1/audit?limit=20", ct);
        var auditBody = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, auditBody);
        var audit = await response.Content.ReadFromJsonAsync(AcJsonContext.Default.ApiResponseListAuditLogView, ct);
        audit!.Data.ShouldContain(entry => entry.Action == "command.issue" && entry.Target == _machineId.Value);
    }

    private async Task WaitForRequestCountAsync(int expected, CancellationToken ct)
    {
        var time = _host.Services.GetRequiredService<TimeProvider>();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (_agent.RequestCount >= expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), time, ct);
        }

        throw new TimeoutException($"The agent did not receive {expected} request(s).");
    }

    private async Task WaitForDisconnectAsync(CancellationToken ct)
    {
        var registry = _host.Services.GetRequiredService<ConnectionRegistry>();
        var time = _host.Services.GetRequiredService<TimeProvider>();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (registry.GetConnection(_machineId.Value) is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), time, ct);
        }

        throw new TimeoutException("The agent connection was not removed.");
    }

    [Fact]
    public async Task Vpn_status_returns_live_agent_state()
    {
        var ct = TestContext.Current.CancellationToken;
        string? dispatchedType = null;
        _agent.Responder = packet =>
        {
            dispatchedType = packet.Type;
            return Task.FromResult<ResponsePacket?>(Response(
                packet,
                JsonDocument.Parse(
                    """{"cliFound":true,"cliPath":"C:\\bin\\protonvpn.exe","helperAvailable":false,"protonGuiRunning":true,"signedIn":true,"username":"user@example.com","expiresAt":"2027-01-01T00:00:00Z","tunnelUp":true,"server":"JP#1","country":"JP","city":"Tokyo","detail":"Connected","exitIp":"203.0.113.10","statusText":"JP#1 연결됨","busyOperation":null,"busyCommandId":null,"asOf":"2026-01-01T00:00:00Z"}""")
                    .RootElement.Clone()));
        };

        using var response = await _browser.GetAsync(
            $"/api/v1/machines/{_machineId.Value}/vpn/status",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("meta").GetProperty("source").GetString().ShouldBe(MetaSources.Live);
        parsed.RootElement.GetProperty("data").GetProperty("signedIn").GetBoolean().ShouldBeTrue();
        parsed.RootElement.GetProperty("data").GetProperty("server").GetString().ShouldBe("JP#1");
        dispatchedType.ShouldBe(QueryTypes.VpnStatus);
    }

    [Fact]
    public async Task Vpn_connect_dispatches_command_and_returns_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        string? dispatchedType = null;
        string? dispatchedServer = null;
        _agent.Responder = packet =>
        {
            dispatchedType = packet.Type;
            dispatchedServer = packet.Payload.GetProperty("server").GetString();
            return Task.FromResult<ResponsePacket?>(Response(
                packet,
                JsonDocument.Parse(
                    """{"connect":{"server":"JP#1","country":"JP","countryName":"Japan","city":"Tokyo","load":12,"exitIp":"203.0.113.10","connectedAt":"2026-01-01T00:00:00Z"},"actionText":"JP#1 연결됨","status":{"cliFound":true,"cliPath":"protonvpn","helperAvailable":false,"protonGuiRunning":true,"signedIn":true,"username":"user@example.com","expiresAt":null,"tunnelUp":true,"server":"JP#1","country":"JP","city":"Tokyo","detail":"Connected","exitIp":"203.0.113.10","statusText":"JP#1 연결됨","busyOperation":null,"busyCommandId":null,"asOf":"2026-01-01T00:00:00Z"},"servers":null}""")
                    .RootElement.Clone()));
        };

        using var response = await _browser.PostAsJsonAsync(
            $"/api/v1/machines/{_machineId.Value}/vpn/connect",
            new { server = "JP#1" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("data").GetProperty("result").GetProperty("actionText")
            .GetString().ShouldBe("JP#1 연결됨");
        dispatchedType.ShouldBe(CommandTypes.VpnConnect);
        dispatchedServer.ShouldBe("JP#1");
    }

    [Fact]
    public async Task Vpn_policy_returns_live_agent_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        string? dispatchedType = null;
        _agent.Responder = packet =>
        {
            dispatchedType = packet.Type;
            return Task.FromResult<ResponsePacket?>(Response(
                packet,
                JsonDocument.Parse(
                    """{"enabled":true,"minLoad":0,"maxLoad":70,"excludeUsedWithinHours":24,"maxAttemptsPerSession":3,"retryWaitThresholdSeconds":180,"countries":[],"currentServer":"JP#1","updatedAt":"2026-01-01T00:00:00Z","servers":[]}""")
                    .RootElement.Clone()));
        };

        using var response = await _browser.GetAsync(
            $"/api/v1/machines/{_machineId.Value}/vpn/policy",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("meta").GetProperty("source").GetString().ShouldBe(MetaSources.Live);
        parsed.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        parsed.RootElement.GetProperty("data").GetProperty("maxLoad").GetInt32().ShouldBe(70);
        dispatchedType.ShouldBe(QueryTypes.VpnPolicy);
    }

    [Fact]
    public async Task Vpn_policy_update_dispatches_policy_command()
    {
        var ct = TestContext.Current.CancellationToken;
        string? dispatchedType = null;
        bool? dispatchedEnabled = null;
        string? dispatchedCountries = null;
        _agent.Responder = packet =>
        {
            dispatchedType = packet.Type;
            dispatchedEnabled = packet.Payload.GetProperty("enabled").GetBoolean();
            dispatchedCountries = packet.Payload.GetProperty("countries").GetRawText();
            return Task.FromResult<ResponsePacket?>(Response(
                packet,
                JsonDocument.Parse("""{"enabled":false}""").RootElement.Clone()));
        };

        using var response = await _browser.PutAsJsonAsync(
            $"/api/v1/machines/{_machineId.Value}/vpn/policy?wait=5",
            new
            {
                enabled = false,
                minLoad = 5,
                maxLoad = 55,
                excludeUsedWithinHours = 12,
                maxAttemptsPerSession = 4,
                retryWaitThresholdSeconds = 120,
                countries = new[] { "JP", "NL" },
            },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        dispatchedType.ShouldBe(CommandTypes.VpnPolicySet);
        dispatchedEnabled.ShouldBe(false);
        dispatchedCountries.ShouldBe("""["JP","NL"]""");
    }

    private static ResponsePacket Response(RequestPacket packet, JsonElement data) =>
        new(
            packet.RequestId,
            ResponseStatus.Succeeded,
            data,
            null,
            null,
            TimeProvider.System.GetUtcNow(),
            1);

    private sealed class TestAgent : IAsyncDisposable
    {
        private readonly HubConnection _connection;

        private TestAgent(HubConnection connection)
        {
            _connection = connection;
        }

        public Func<RequestPacket, Task<ResponsePacket?>> Responder { get; set; } =
            _ => Task.FromResult<ResponsePacket?>(null);

        public int RequestCount { get; private set; }

        public static async Task<TestAgent> ConnectAsync(
            string baseUrl,
            MachineId machineId,
            string token,
            CancellationToken ct)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl}/hubs/agent", options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();
            var agent = new TestAgent(connection);
            connection.On<RequestPacket>("Request", agent.HandleAsync);
            await connection.StartAsync(ct);
            await connection.InvokeAsync<HelloAck>(
                "Hello",
                new HelloRequest(machineId, "test", "boot-1", 0, Array.Empty<string>()),
                ct);
            return agent;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        private async Task HandleAsync(RequestPacket packet)
        {
            RequestCount++;
            if (packet.Kind == RequestKind.Command)
            {
                await _connection.InvokeAsync(
                    "Ack",
                    new AckPacket(packet.RequestId, TimeProvider.System.GetUtcNow()))
                    .ConfigureAwait(false);
            }

            var response = await Responder(packet).ConfigureAwait(false);
            if (response is not null)
            {
                await _connection.InvokeAsync("Respond", response).ConfigureAwait(false);
            }
        }
    }
}
