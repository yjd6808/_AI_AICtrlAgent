using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Runtime;

public sealed class ChannelAcEventBusTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Ping(string Tag) : AcEvent;

    private sealed record Pong : AcEvent;

    private static Ping NewPing(string tag) => new(tag)
    {
        EventId = Guid.NewGuid().ToString("N"),
        OccurredAt = FixedTime,
    };

    private static Pong NewPong() => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        OccurredAt = FixedTime,
    };

    [Fact]
    public async Task Publish_delivers_event_to_subscriber()
    {
        var bus = new ChannelAcEventBus(NullLoggerFactory.Instance);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = bus.Subscribe<Ping>((e, ct) =>
        {
            received.TrySetResult(e.Tag);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(NewPing("hello"), TestContext.Current.CancellationToken);

        var tag = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        tag.ShouldBe("hello");
    }

    [Fact]
    public async Task Failing_handler_does_not_affect_other_subscribers()
    {
        var bus = new ChannelAcEventBus(NullLoggerFactory.Instance);
        var survivorReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var failing = bus.Subscribe<Ping>((e, ct) => throw new InvalidOperationException("boom"));
        await using var survivor = bus.Subscribe<Ping>((e, ct) =>
        {
            survivorReceived.TrySetResult(e.Tag);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(NewPing("still-works"), TestContext.Current.CancellationToken);

        var tag = await survivorReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        tag.ShouldBe("still-works");
    }

    [Fact]
    public async Task Disposed_subscription_receives_nothing()
    {
        var bus = new ChannelAcEventBus(NullLoggerFactory.Instance);
        var disposedCount = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = bus.Subscribe<Ping>((e, ct) =>
        {
            Interlocked.Increment(ref disposedCount);
            return ValueTask.CompletedTask;
        });

        await using var watcher = bus.Subscribe<Ping>((e, ct) =>
        {
            if (e.Tag == "barrier")
            {
                barrier.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });

        await subscription.DisposeAsync();

        await bus.PublishAsync(NewPing("first"), TestContext.Current.CancellationToken);
        await bus.PublishAsync(NewPing("barrier"), TestContext.Current.CancellationToken);

        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref disposedCount).ShouldBe(0);
    }

    [Fact]
    public async Task Other_event_types_are_not_delivered()
    {
        var bus = new ChannelAcEventBus(NullLoggerFactory.Instance);
        var pingCount = 0;
        var pongReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pingSubscriber = bus.Subscribe<Ping>((e, ct) =>
        {
            Interlocked.Increment(ref pingCount);
            return ValueTask.CompletedTask;
        });

        await using var pongSubscriber = bus.Subscribe<Pong>((e, ct) =>
        {
            pongReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(NewPong(), TestContext.Current.CancellationToken);

        await pongReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref pingCount).ShouldBe(0);
    }
}
