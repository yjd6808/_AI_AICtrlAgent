using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AiCtrl.FakeOpenCode;

public sealed class FakeSseHub
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly List<Channel<string>> _channels = new();
    private long _seq;

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _channels.Count;
            }
        }
    }

    public (ChannelReader<string> Reader, IDisposable Lease) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (_gate)
        {
            _channels.Add(channel);
        }

        return (channel.Reader, new Lease(this, channel));
    }

    public void Publish(string type, object properties)
    {
        string json;
        lock (_gate)
        {
            _seq++;
            json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = "evt_" + _seq.ToString(),
                ["type"] = type,
                ["properties"] = properties,
            }, Json);

            foreach (var channel in _channels)
            {
                channel.Writer.TryWrite(json);
            }
        }
    }

    private void Remove(Channel<string> channel)
    {
        lock (_gate)
        {
            _channels.Remove(channel);
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly FakeSseHub _hub;
        private readonly Channel<string> _channel;
        private int _disposed;

        public Lease(FakeSseHub hub, Channel<string> channel)
        {
            _hub = hub;
            _channel = channel;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _channel.Writer.TryComplete();
                _hub.Remove(_channel);
            }
        }
    }
}
