using System.Net;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class UiHubTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly CookieContainer _cookies = new();

    private readonly HttpClientHandler _handler;

    private readonly HttpClient _browser;

    private TestServerHost _host = null!;

    public UiHubTests()
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        _browser = new HttpClient(_handler);
    }

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(_directory);
        _browser.BaseAddress = new Uri(_host.BaseUrl);
        using var login = await _browser.PostAsync(
            "/test-login?email=test%40example.com&name=Test+User",
            null,
            TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        _handler.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Unauthenticated_ui_negotiate_is_redirected_to_login()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var anonymous = new HttpClient(handler);
        using var response = await anonymous.PostAsync(
            new Uri(_host.BaseUrl + "/hubs/ui/negotiate?negotiateVersion=1"),
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location?.AbsolutePath.ShouldBe("/api/v1/auth/login");
    }

    [Fact]
    public async Task Connected_ui_client_receives_fleet_snapshot_and_watched_stream_chunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = new TaskCompletionSource<FleetSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new TaskCompletionSource<StreamChunk[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = BuildConnection();
        connection.On<FleetSnapshotDto>("FleetSnapshot", value => snapshot.TrySetResult(value));
        connection.On<MachineId, string, long, StreamChunk[]>(
            "StreamChunks",
            (_, _, _, value) => chunks.TrySetResult(value));

        await connection.StartAsync(ct);
        var connectedSnapshot = await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        connectedSnapshot.Machines.ShouldNotBeNull();

        var machine = new MachineId("machine-ui-test");
        await connection.InvokeAsync("WatchSession", machine, "session-1", ct);

        var streams = _host.Services.GetRequiredService<SessionStreamHub>();
        var expected = new StreamChunk(
            "message-1",
            "part-1",
            "text",
            7,
            StreamOp.Append,
            "hello",
            null);
        await streams.PublishAsync(
            new StreamBatch(machine, "session-1", 3, [expected]),
            ct);

        var received = await chunks.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        received.ShouldHaveSingleItem();
        received[0].MessageId.ShouldBe("message-1");
        received[0].Seq.ShouldBe(7);
    }

    [Fact]
    public async Task Unwatched_stream_chunks_are_not_delivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = new TaskCompletionSource<FleetSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new TaskCompletionSource<StreamChunk[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = BuildConnection();
        connection.On<FleetSnapshotDto>("FleetSnapshot", value => snapshot.TrySetResult(value));
        connection.On<MachineId, string, long, StreamChunk[]>(
            "StreamChunks",
            (_, _, _, value) => chunks.TrySetResult(value));

        await connection.StartAsync(ct);
        await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var machine = new MachineId("machine-ui-test");
        var streams = _host.Services.GetRequiredService<SessionStreamHub>();
        await streams.PublishAsync(
            new StreamBatch(
                machine,
                "session-1",
                3,
                [new StreamChunk("message-1", "part-1", "text", 7, StreamOp.Append, "hello", null)]),
            ct);

        await Task.WhenAny(chunks.Task, Task.Delay(TimeSpan.FromMilliseconds(500), ct));
        chunks.Task.IsCompleted.ShouldBeFalse("unwatched connection must not receive stream chunks");
    }

    private HubConnection BuildConnection() =>
        new HubConnectionBuilder()
            .WithUrl(_host.BaseUrl + "/hubs/ui", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => _handler;
                options.Cookies = _cookies;
            })
            .Build();
}
