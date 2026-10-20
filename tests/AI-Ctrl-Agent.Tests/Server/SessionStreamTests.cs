using System.Net;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Streams;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class SessionStreamTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private readonly CookieContainer _cookies = new();

    private readonly HttpClientHandler _handler;

    private readonly HttpClient _browser;

    private TestServerHost _host = null!;

    public SessionStreamTests()
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
    public async Task Publish_then_read_returns_chunks_and_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var streams = Streams();
        await streams.PublishAsync(Batch(7, Chunk(1, "a"), Chunk(2, "b")), ct);

        var first = await streams.ReadAsync(Machine, Session, null, TimeSpan.Zero, ct);
        first.CursorExpired.ShouldBeFalse();
        first.Epoch.ShouldBe(7);
        first.NextCursor.ShouldBe("7:2");
        first.Chunks.Count.ShouldBe(2);

        var none = await streams.ReadAsync(Machine, Session, "7:2", TimeSpan.Zero, ct);
        none.Chunks.ShouldBeEmpty();
        none.NextCursor.ShouldBe("7:2");

        await streams.PublishAsync(Batch(7, Chunk(3, "c")), ct);
        var next = await streams.ReadAsync(Machine, Session, "7:2", TimeSpan.Zero, ct);
        next.Chunks.ShouldHaveSingleItem();
        next.Chunks[0].Text.ShouldBe("c");
        next.NextCursor.ShouldBe("7:3");
    }

    [Fact]
    public async Task Long_poll_wakes_when_a_chunk_is_published()
    {
        var ct = TestContext.Current.CancellationToken;
        var streams = Streams();
        await streams.PublishAsync(Batch(7, Chunk(1, "a")), ct);

        var pending = streams.ReadAsync(Machine, Session, "7:1", TimeSpan.FromSeconds(5), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        await streams.PublishAsync(Batch(7, Chunk(2, "b")), ct);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);
        result.Chunks.ShouldHaveSingleItem();
        result.Chunks[0].Text.ShouldBe("b");
        result.NextCursor.ShouldBe("7:2");
    }

    [Fact]
    public async Task Epoch_change_expires_the_previous_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var streams = Streams();
        await streams.PublishAsync(Batch(7, Chunk(1, "old")), ct);
        await streams.PublishAsync(Batch(8, Chunk(1, "new")), ct);

        var result = await streams.ReadAsync(Machine, Session, "7:1", TimeSpan.Zero, ct);
        result.CursorExpired.ShouldBeTrue();
        result.Epoch.ShouldBe(8);
        result.NextCursor.ShouldBe("8:1");
        result.Chunks.ShouldHaveSingleItem();
        result.Chunks[0].Text.ShouldBe("new");
    }

    [Fact]
    public async Task Stale_epoch_and_duplicate_sequence_are_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var streams = Streams();
        await streams.PublishAsync(Batch(5, Chunk(1, "first")), ct);
        await streams.PublishAsync(Batch(4, Chunk(2, "stale")), ct);
        await streams.PublishAsync(Batch(5, Chunk(1, "duplicate")), ct);

        var result = await streams.ReadAsync(Machine, Session, null, TimeSpan.Zero, ct);
        result.Epoch.ShouldBe(5);
        result.Chunks.ShouldHaveSingleItem();
        result.Chunks[0].Text.ShouldBe("first");
        result.NextCursor.ShouldBe("5:1");
    }

    [Fact]
    public async Task Rest_stream_endpoint_returns_chunks_and_expired_cursor_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var streams = Streams();
        await streams.PublishAsync(Batch(9, Chunk(1, "rest")), ct);

        var url = $"/api/v1/machines/{Machine}/sessions/{Session}/stream?waitMs=0";
        using var response = await _browser.GetAsync(url, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(ct),
            AcJsonContext.Default.ApiResponseStreamReadView,
            ct);
        payload.ShouldNotBeNull();
        payload.Data.Epoch.ShouldBe(9);
        payload.Data.NextCursor.ShouldBe("9:1");
        payload.Data.CursorExpired.ShouldBeFalse();
        payload.Data.Chunks.ShouldHaveSingleItem();
        payload.Meta.Stale.ShouldBeFalse();

        await streams.PublishAsync(Batch(10, Chunk(1, "next")), ct);
        var expiredUrl = $"/api/v1/machines/{Machine}/sessions/{Session}/stream?cursor=9%3A1&waitMs=0";
        using var expiredResponse = await _browser.GetAsync(expiredUrl, ct);
        var expired = await JsonSerializer.DeserializeAsync(
            await expiredResponse.Content.ReadAsStreamAsync(ct),
            AcJsonContext.Default.ApiResponseStreamReadView,
            ct);
        expired.ShouldNotBeNull();
        expired.Data.CursorExpired.ShouldBeTrue();
        expired.Meta.Stale.ShouldBeTrue();
        expired.Data.Chunks.ShouldHaveSingleItem();
        expired.Data.Chunks[0].Text.ShouldBe("next");
    }

    private const string Machine = "machine-stream";

    private const string Session = "session-stream";

    private SessionStreamHub Streams() => _host.Services.GetRequiredService<SessionStreamHub>();

    private static StreamBatch Batch(long epoch, params StreamChunk[] chunks) =>
        new(new MachineId(Machine), Session, epoch, chunks);

    private static StreamChunk Chunk(long seq, string text) =>
        new("message-1", "part-1", "text", seq, StreamOp.Append, text, null);
}

public sealed class SessionStreamRingTests : IAsyncLifetime
{
    private readonly string _directory = TestServerHost.CreateDirectory();

    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(
            _directory,
            streamOptions: new StreamOptions { RingChunks = 2 });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Ring_trims_old_chunks_and_flags_a_cursor_before_the_ring()
    {
        var ct = TestContext.Current.CancellationToken;
        var streams = _host.Services.GetRequiredService<SessionStreamHub>();
        var machine = new MachineId("machine-ring");
        await streams.PublishAsync(
            new StreamBatch(
                machine,
                "session-ring",
                1,
                [Chunk(1), Chunk(2), Chunk(3)]),
            ct);

        var all = await streams.ReadAsync("machine-ring", "session-ring", null, TimeSpan.Zero, ct);
        all.Chunks.Count.ShouldBe(2);
        all.Chunks[0].Seq.ShouldBe(2);
        all.NextCursor.ShouldBe("1:3");

        var expired = await streams.ReadAsync("machine-ring", "session-ring", "1:0", TimeSpan.Zero, ct);
        expired.CursorExpired.ShouldBeTrue();
        expired.Chunks.Count.ShouldBe(2);

        var contiguous = await streams.ReadAsync("machine-ring", "session-ring", "1:1", TimeSpan.Zero, ct);
        contiguous.CursorExpired.ShouldBeFalse();
        contiguous.Chunks.Count.ShouldBe(2);
    }

    private static StreamChunk Chunk(long seq) =>
        new("message-1", "part-1", "text", seq, StreamOp.Append, "x", null);
}
