using System.Net.NetworkInformation;

namespace AiCtrl.Client.Link;

public interface INetworkChangeSignal
{
    Task WaitAsync(CancellationToken ct);
}

public sealed class NetworkChangeSignal : INetworkChangeSignal, IDisposable
{
    private readonly List<TaskCompletionSource> _waiters = new();

    private readonly object _gate = new();

    public NetworkChangeSignal()
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add(waiter);
        }

        try
        {
            await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (_gate)
        {
            foreach (var waiter in _waiters)
            {
                waiter.TrySetCanceled();
            }

            _waiters.Clear();
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        TaskCompletionSource[] waiters;
        lock (_gate)
        {
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }
}
