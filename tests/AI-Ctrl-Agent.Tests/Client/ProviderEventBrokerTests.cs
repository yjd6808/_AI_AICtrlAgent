using AiCtrl.Client.Systems;
using AiCtrl.Core.Providers;
using AiCtrl.Tests.Systems;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class ProviderEventBrokerTests
{
    [Fact]
    public void Publish_delivers_events_to_all_subscribers()
    {
        var instance = new FakeProviderInstance();
        var broker = new ProviderEventBroker(NullLogger<ProviderEventBroker>.Instance);
        var first = new List<string>();
        var second = new List<string>();
        using var firstSubscription = broker.Subscribe((_, providerEvent) => first.Add(providerEvent.Kind));
        using var secondSubscription = broker.Subscribe((_, providerEvent) => second.Add(providerEvent.Kind));

        broker.Publish(instance, new ProviderEvent("message.updated", null));

        first.ShouldBe(new[] { "message.updated" });
        second.ShouldBe(new[] { "message.updated" });
    }

    [Fact]
    public void Failing_subscriber_does_not_block_others()
    {
        var instance = new FakeProviderInstance();
        var broker = new ProviderEventBroker(NullLogger<ProviderEventBroker>.Instance);
        var received = new List<string>();
        using var failing = broker.Subscribe((_, _) => throw new InvalidOperationException("boom"));
        using var survivor = broker.Subscribe((_, providerEvent) => received.Add(providerEvent.Kind));

        Should.NotThrow(() => broker.Publish(instance, new ProviderEvent("session.idle", null)));

        received.ShouldBe(new[] { "session.idle" });
    }

    [Fact]
    public void Disposed_subscription_stops_receiving_events()
    {
        var instance = new FakeProviderInstance();
        var broker = new ProviderEventBroker(NullLogger<ProviderEventBroker>.Instance);
        var received = new List<string>();
        var subscription = broker.Subscribe((_, providerEvent) => received.Add(providerEvent.Kind));

        subscription.Dispose();
        broker.Publish(instance, new ProviderEvent("session.idle", null));

        received.ShouldBeEmpty();
    }
}
