using AiCtrl.Core.Providers;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed class ProviderEventBroker
{
    private readonly object _gate = new();

    private readonly List<Subscription> _subscriptions = new();

    private readonly ILogger<ProviderEventBroker> _logger;

    public ProviderEventBroker(ILogger<ProviderEventBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public IDisposable Subscribe(Action<IProviderInstance, ProviderEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(this, handler);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish(IProviderInstance instance, ProviderEvent providerEvent)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(providerEvent);

        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            try
            {
                subscription.Handler(instance, providerEvent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider event subscriber failed for {Kind}.", providerEvent.Kind);
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ProviderEventBroker _owner;

        private int _disposed;

        public Subscription(ProviderEventBroker owner, Action<IProviderInstance, ProviderEvent> handler)
        {
            _owner = owner;
            Handler = handler;
        }

        public Action<IProviderInstance, ProviderEvent> Handler { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Remove(this);
            }
        }
    }
}
