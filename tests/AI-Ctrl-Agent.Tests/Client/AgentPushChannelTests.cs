using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class AgentPushChannelTests
{
    private static readonly StreamBatch Batch = new(
        new MachineId("m_test"),
        "ses_1",
        1,
        new[] { new StreamChunk("msg_1", "prt_1", "text", 1, StreamOp.Replace, "hello", null) });

    [Fact]
    public async Task Send_without_attachment_returns_false()
    {
        var channel = Create();

        (await channel.TrySendAsync(Batch, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Attached_sender_receives_batches()
    {
        var channel = Create();
        StreamBatch? received = null;
        channel.Attach((batch, _) =>
        {
            received = batch;
            return Task.CompletedTask;
        });

        (await channel.TrySendAsync(Batch, TestContext.Current.CancellationToken)).ShouldBeTrue();

        received.ShouldBeSameAs(Batch);
    }

    [Fact]
    public async Task Sender_failure_returns_false()
    {
        var channel = Create();
        channel.Attach((_, _) => throw new InvalidOperationException("offline"));

        (await channel.TrySendAsync(Batch, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public void Detach_only_clears_the_matching_sender()
    {
        var channel = Create();
        Func<StreamBatch, CancellationToken, Task> first = (_, _) => Task.CompletedTask;
        Func<StreamBatch, CancellationToken, Task> second = (_, _) => Task.CompletedTask;
        channel.Attach(first);
        channel.Attach(second);

        channel.Detach(first);
        channel.IsConnected.ShouldBeTrue();

        channel.Detach(second);
        channel.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var channel = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        channel.Attach((_, token) => Task.FromCanceled(token));

        await Should.ThrowAsync<OperationCanceledException>(() => channel.TrySendAsync(Batch, cts.Token));
    }

    private static AgentPushChannel Create() =>
        new(NullLogger<AgentPushChannel>.Instance);
}
