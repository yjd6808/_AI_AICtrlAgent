using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class EnrollmentAndAuthTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestServerHost.StartAsync(_directory);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Enrollment_code_has_eight_symbols_and_one_separator()
    {
        var code = EnrollmentCodeFormat.Generate(_ => 0);

        code.ShouldBe("AAAA-AAAA");
        EnrollmentCodeFormat.Normalize(code).Length.ShouldBe(EnrollmentCodeFormat.Length);
    }

    [Fact]
    public async Task Enrollment_code_is_single_use_and_normalized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = NewKey();
        var code = await _host.Services.GetRequiredService<EnrollmentService>().CreateCodeAsync(ct);
        var request = Request(code.Code.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(), key);

        using var http = new HttpClient();
        using var first = await http.PostAsJsonAsync($"{_host.BaseUrl}/api/v1/enroll", request, AcJsonContext.Default.EnrollmentRequest, ct);
        using var second = await http.PostAsJsonAsync($"{_host.BaseUrl}/api/v1/enroll", request, AcJsonContext.Default.EnrollmentRequest, ct);

        first.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        second.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var problem = await second.Content.ReadFromJsonAsync(AcJsonContext.Default.AcProblemDetails, ct);
        problem!.Code.ShouldBe(AcErrorCodes.InvalidCode);
    }

    [Fact]
    public async Task Expired_enrollment_code_returns_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = NewKey();
        var db = _host.Services.GetRequiredService<ServerDb>();
        const string code = "AAAA-AAAA";
        await db.CreateEnrollmentCodeAsync(
            EnrollmentService.Hash(EnrollmentCodeFormat.Normalize(code)),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            ct);

        using var http = new HttpClient();
        using var response = await http.PostAsJsonAsync(
            $"{_host.BaseUrl}/api/v1/enroll",
            Request(code, key),
            AcJsonContext.Default.EnrollmentRequest,
            ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Challenge_token_is_valid_and_nonce_cannot_be_replayed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = NewKey();
        var machineId = await EnrollAsync(key, ct);
        using var api = new AiCtrl.Client.Link.ServerApiClient(_host.BaseUrl);

        var challenge = await api.ChallengeAsync(machineId, ct);
        var request = new AgentTokenRequest(
            machineId,
            challenge.Nonce,
            Convert.ToBase64String(key.SignData(Payload(machineId, challenge.Nonce), HashAlgorithmName.SHA256)));
        var token = await api.TokenAsync(request, ct);
        var replay = await Should.ThrowAsync<AiCtrl.Client.Link.AcApiException>(() => api.TokenAsync(request, ct));
        replay.Code.ShouldBe(AcErrorCodes.ChallengeExpired);

        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
        token.ExpiresIn.ShouldBe(AgentTokenDefaults.LifetimeSeconds);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = NewKey();
        using var other = NewKey();
        var machineId = await EnrollAsync(key, ct);
        var auth = _host.Services.GetRequiredService<AgentAuthService>();

        var challenge = await auth.ChallengeAsync(machineId, ct);
        var signature = Convert.ToBase64String(other.SignData(Payload(machineId, challenge.Value!), HashAlgorithmName.SHA256));
        var result = await auth.IssueTokenAsync(
            new AgentTokenRequest(machineId, challenge.Value!, signature),
            ct);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe(AcErrorCodes.InvalidSignature);
    }

    [Fact]
    public async Task Agent_token_connects_to_hub_and_hello_reports_snapshot_state()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = NewKey();
        var machineId = await EnrollAsync(key, ct);
        var token = await GetTokenAsync(key, machineId, ct);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{_host.BaseUrl}/hubs/agent", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await connection.StartAsync(ct);
        var ack = await connection.InvokeAsync<HelloAck>(
            "Hello",
            new HelloRequest(machineId, "test", "boot-1", 0, Array.Empty<string>()),
            ct);

        ack.SnapshotRequired.ShouldBeFalse();
        ack.ObservedCountry.ShouldBeNull();
    }

    [Fact]
    public async Task Server_deduplicates_replayed_event_sequences()
    {
        var ct = TestContext.Current.CancellationToken;
        using var key = NewKey();
        var machineId = await EnrollAsync(key, ct);
        var db = _host.Services.GetRequiredService<ServerDb>();
        using var payload = System.Text.Json.JsonDocument.Parse("{}");
        var events = new[]
        {
            new Core.Events.AgentEventEnvelope(1, "test", DateTimeOffset.UtcNow, payload.RootElement.Clone()),
        };

        (await db.ApplyEventsAsync(machineId.Value, events, ct)).LastAppliedEventSeq.ShouldBe(1);
        (await db.ApplyEventsAsync(machineId.Value, events, ct)).LastAppliedEventSeq.ShouldBe(1);
        var machine = await db.GetMachineAsync(machineId.Value, ct);
        machine!.LastAppliedEventSeq.ShouldBe(1);
    }

    private static EnrollmentRequest Request(string code, ECDsa key) =>
        new(
            code,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            "test-host",
            "test-os",
            "1.0.0");

    private async Task<MachineId> EnrollAsync(ECDsa key, CancellationToken ct)
    {
        var service = _host.Services.GetRequiredService<EnrollmentService>();
        var code = await service.CreateCodeAsync(ct);
        var result = await service.EnrollAsync(Request(code.Code, key), "test", ct);
        result.Success.ShouldBeTrue($"enroll failed: {result.ErrorCode}");
        return new MachineId(result.MachineId!);
    }

    private async Task<string> GetTokenAsync(ECDsa key, MachineId machineId, CancellationToken ct)
    {
        var auth = _host.Services.GetRequiredService<AgentAuthService>();
        var challenge = await auth.ChallengeAsync(machineId, ct);
        var signature = Convert.ToBase64String(key.SignData(Payload(machineId, challenge.Value!), HashAlgorithmName.SHA256));
        var token = await auth.IssueTokenAsync(
            new AgentTokenRequest(machineId, challenge.Value!, signature),
            ct);
        token.Success.ShouldBeTrue();
        return token.Value!;
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static byte[] Payload(MachineId machineId, string nonce) =>
        Encoding.UTF8.GetBytes($"aictrl-agent-v1|{machineId.Value}|{nonce}");
}
