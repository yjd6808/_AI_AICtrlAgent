using System.Threading.Channels;
using AiCtrl.Core.Runtime;
using Shouldly;

namespace AiCtrl.Tests.Runtime;

public sealed class AcFluxTests
{
    private sealed record CounterState(int Value);

    private sealed record Add(int Amount) : IAcAction;

    private static AcFlux<CounterState> CreateFlux() =>
        new(new CounterState(0), (state, action) =>
            action is Add add ? state with { Value = state.Value + add.Amount } : state);

    [Fact]
    public async Task Reducer_applies_actions_in_order()
    {
        await using var flux = CreateFlux();

        await flux.DispatchAsync(new Add(1), TestContext.Current.CancellationToken);
        await flux.DispatchAsync(new Add(2), TestContext.Current.CancellationToken);
        await flux.DispatchAsync(new Add(4), TestContext.Current.CancellationToken);

        await RuntimeTestHelpers.WaitUntilAsync(() => flux.Current.Value == 7);
        flux.Current.Value.ShouldBe(7);
    }

    [Fact]
    public async Task Watch_delivers_latest_state()
    {
        await using var flux = CreateFlux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = flux.Watch(cts.Token).GetAsyncEnumerator(cts.Token);

        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            enumerator.Current.Value.ShouldBe(0);

            for (var i = 0; i < 50; i++)
            {
                await flux.DispatchAsync(new Add(1), TestContext.Current.CancellationToken);
            }

            var sawLatest = false;
            while (!sawLatest && await enumerator.MoveNextAsync())
            {
                sawLatest = enumerator.Current.Value == 50;
            }

            sawLatest.ShouldBeTrue();
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispatch_after_dispose_throws()
    {
        var flux = CreateFlux();
        await flux.DisposeAsync();

        await Should.ThrowAsync<ChannelClosedException>(async () => await flux.DispatchAsync(new Add(1)));
    }
}
