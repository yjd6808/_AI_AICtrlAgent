using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AiCtrl.Core.Runtime;

public sealed class AcFlux<TState> : IAsyncDisposable where TState : class
{
    private readonly Func<TState, IAcAction, TState> _reducer;
    private readonly Channel<IAcAction> _actions;
    private readonly ConcurrentDictionary<ChannelWriter<TState>, byte> _watchers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private TState _current;

    public AcFlux(TState initial, Func<TState, IAcAction, TState> reducer)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(reducer);

        _current = initial;
        _reducer = reducer;
        _actions = Channel.CreateUnbounded<IAcAction>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _loop = Task.Run(RunAsync);
    }

    public TState Current => Volatile.Read(ref _current);

    public ValueTask DispatchAsync(IAcAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _actions.Writer.WriteAsync(action, ct);
    }

    public async IAsyncEnumerable<TState> Watch([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<TState>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _watchers.TryAdd(channel.Writer, 0);
        try
        {
            channel.Writer.TryWrite(Current);

            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var state))
                {
                    yield return state;
                }
            }
        }
        finally
        {
            _watchers.TryRemove(channel.Writer, out _);
            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _actions.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var action in _actions.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var next = _reducer(Current, action);
                Volatile.Write(ref _current, next);

                foreach (var watcher in _watchers.Keys)
                {
                    watcher.TryWrite(next);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
