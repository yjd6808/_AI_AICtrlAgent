using System.Security.Cryptography;
using System.Text.Json;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Projection;
using AiCtrl.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class VpnStateTests : IAsyncLifetime
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
    public async Task Vpn_state_is_persisted_and_projected_into_machine_view()
    {
        var ct = TestContext.Current.CancellationToken;
        var machineId = await EnrollAsync(ct);
        var db = _host.Services.GetRequiredService<ServerDb>();
        var state = Status("JP#7", "203.0.113.7");
        var result = await db.ApplyEventsAsync(
            machineId,
            new[] { Event(1, state) },
            ct);

        result.LastAppliedEventSeq.ShouldBe(1);
        result.VpnStates.ShouldHaveSingleItem().Server.ShouldBe("JP#7");

        var stored = await db.GetVpnStateAsync(machineId, ct);
        stored.ShouldNotBeNull();
        stored.Server.ShouldBe("JP#7");
        stored.ExitIp.ShouldBe("203.0.113.7");

        var machine = await db.GetMachineAsync(machineId, ct);
        var view = MachineViewFactory.Create(
            machine!,
            new ConnectionRegistry(TimeProvider.System),
            stored);
        view.Vpn.ShouldNotBeNull();
        view.Vpn.Server.ShouldBe("JP#7");
        view.ObservedIp.ShouldBe("203.0.113.7");
        view.ObservedIpSource.ShouldBe("client-reported");
    }

    [Fact]
    public async Task Multiple_vpn_events_in_one_batch_apply_latest_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var machineId = await EnrollAsync(ct);
        var db = _host.Services.GetRequiredService<ServerDb>();

        var result = await db.ApplyEventsAsync(
            machineId,
            new[]
            {
                Event(1, Status("JP#1", "203.0.113.1")),
                Event(2, Status("JP#2", "203.0.113.2"))
            },
            ct);

        result.LastAppliedEventSeq.ShouldBe(2);
        result.VpnStates.ShouldHaveSingleItem().Server.ShouldBe("JP#2");

        var stored = await db.GetVpnStateAsync(machineId, ct);
        stored.ShouldNotBeNull();
        stored.Server.ShouldBe("JP#2");
        stored.ExitIp.ShouldBe("203.0.113.2");
    }

    [Fact]
    public async Task Replayed_and_malformed_vpn_events_do_not_replace_newer_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var machineId = await EnrollAsync(ct);
        var db = _host.Services.GetRequiredService<ServerDb>();

        await db.ApplyEventsAsync(machineId, new[] { Event(2, Status("JP#new", null)) }, ct);
        var replay = await db.ApplyEventsAsync(machineId, new[] { Event(1, Status("JP#old", null)) }, ct);
        replay.VpnStates.ShouldBeEmpty();

        using var malformed = JsonDocument.Parse("{}");
        var malformedResult = await db.ApplyEventsAsync(
            machineId,
            new[] { new AgentEventEnvelope(3, AgentEventTypes.VpnState, DateTimeOffset.UtcNow, malformed.RootElement.Clone()) },
            ct);
        malformedResult.VpnStates.ShouldBeEmpty();
        malformedResult.LastAppliedEventSeq.ShouldBe(3);

        var stored = await db.GetVpnStateAsync(machineId, ct);
        stored.ShouldNotBeNull();
        stored.Server.ShouldBe("JP#new");
    }

    private async Task<string> EnrollAsync(CancellationToken ct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var service = _host.Services.GetRequiredService<EnrollmentService>();
        var code = await service.CreateCodeAsync(ct);
        var result = await service.EnrollAsync(
            new EnrollmentRequest(
                code.Code,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                "vpn-host",
                "test-os",
                "1.0.0"),
            "test",
            ct);
        result.Success.ShouldBeTrue($"enroll failed: {result.ErrorCode}");
        return result.MachineId!;
    }

    private static AgentEventEnvelope Event(long seq, VpnStatusView state) =>
        new(
            seq,
            AgentEventTypes.VpnState,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(state, AcJsonContext.Default.VpnStatusView));

    private static VpnStatusView Status(string server, string? exitIp) =>
        new(
            true,
            "protonvpn.exe",
            false,
            false,
            true,
            "user",
            null,
            true,
            server,
            "JP",
            "Tokyo",
            null,
            exitIp,
            "connected",
            null,
            null,
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
}
