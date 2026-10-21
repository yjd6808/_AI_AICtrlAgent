using AiCtrl.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed class AgentPushChannel
{
    private readonly object _gate = new();

    private readonly ILogger<AgentPushChannel> _logger;

    private Func<StreamBatch, CancellationToken, Task>? _sender;

    public AgentPushChannel(ILogger<AgentPushChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _sender is not null;
            }
        }
    }

    public void Attach(Func<StreamBatch, CancellationToken, Task> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        lock (_gate)
        {
            _sender = sender;
        }
    }

    public void Detach(Func<StreamBatch, CancellationToken, Task> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        lock (_gate)
        {
            if (ReferenceEquals(_sender, sender))
            {
                _sender = null;
            }
        }
    }

    public async Task<bool> TrySendAsync(StreamBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        Func<StreamBatch, CancellationToken, Task>? sender;
        lock (_gate)
        {
            sender = _sender;
        }

        if (sender is null)
        {
            return false;
        }

        try
        {
            await sender(batch, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to push stream chunks for {SessionId}.", batch.SessionId);
            return false;
        }
    }
}
