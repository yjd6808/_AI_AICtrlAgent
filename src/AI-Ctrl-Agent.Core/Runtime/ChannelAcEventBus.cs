using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Runtime;

public sealed class ChannelAcEventBus : IAcEventBus
{
    private const int ChannelCapacity = 1024;

    private readonly ConcurrentDictionary<ISubscription, byte> _subscriptions = new();
    private readonly ILogger<ChannelAcEventBus> _logger;

    public ChannelAcEventBus(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ChannelAcEventBus>();
    }

    public ValueTask PublishAsync(AcEvent e, CancellationToken ct = default)
    {
        var subscriptions = _subscriptions.Keys;
        return subscriptions.Count == 0 ? ValueTask.CompletedTask : PublishCoreAsync(subscriptions, e, ct);
    }

    public IAsyncDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler) where T : AcEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription<T>(this, handler, _logger);
        _subscriptions.TryAdd(subscription, 0);
        subscription.Start();
        return subscription;
    }

    private static async ValueTask PublishCoreAsync(ICollection<ISubscription> subscriptions, AcEvent e, CancellationToken ct)
    {
        foreach (var subscription in subscriptions)
        {
            if (!subscription.Wants(e))
            {
                continue;
            }

            try
            {
                await subscription.EnqueueAsync(e, ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    private void Remove(ISubscription subscription) => _subscriptions.TryRemove(subscription, out _);

    private interface ISubscription
    {
        bool Wants(AcEvent e);

        ValueTask EnqueueAsync(AcEvent e, CancellationToken ct);
    }

    private sealed class Subscription<T> : ISubscription, IAsyncDisposable where T : AcEvent
    {
        private readonly ChannelAcEventBus _owner;
        private readonly Func<T, CancellationToken, ValueTask> _handler;
        private readonly ILogger _logger;
        private readonly Channel<AcEvent> _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pump;
        private int _disposed;

        public Subscription(ChannelAcEventBus owner, Func<T, CancellationToken, ValueTask> handler, ILogger logger)
        {
            _owner = owner;
            _handler = handler;
            _logger = logger;
            _channel = Channel.CreateBounded<AcEvent>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public void Start() => _pump = Task.Run(PumpAsync);

        public bool Wants(AcEvent e) => e is T;

        public ValueTask EnqueueAsync(AcEvent e, CancellationToken ct) => _channel.Writer.WriteAsync(e, ct);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner.Remove(this);
            _channel.Writer.TryComplete();

            if (_pump is not null)
            {
                try
                {
                    await _pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var e in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await _handler((T)e, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Event handler failed for {EventType}", typeof(T).Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
