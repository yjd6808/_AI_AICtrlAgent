using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Runtime;

public sealed class AcLoopSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly List<Task> _tasks = new();
    private readonly object _gate = new();
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private int _disposed;

    public AcLoopSupervisor(TimeProvider time, ILoggerFactory loggerFactory, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _time = time;
        _logger = loggerFactory.CreateLogger<AcLoopSupervisor>();
        _cts = lifetime.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime)
            : new CancellationTokenSource();
    }

    public void Run(string name, Func<CancellationToken, ValueTask> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(body);

        var token = _cts.Token;
        var task = Task.Run(() => RunCoreAsync(name, body, token), CancellationToken.None);

        lock (_gate)
        {
            _tasks.Add(task);
        }
    }

    public ValueTask StopAsync()
    {
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync();
            return new ValueTask(_stopTask);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        _cts.Cancel();

        Task[] pending;
        lock (_gate)
        {
            pending = _tasks.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunCoreAsync(string name, Func<CancellationToken, ValueTask> body, CancellationToken token)
    {
        var backoff = InitialBackoff;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await body(token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background loop '{Loop}' failed; restarting in {Backoff}", name, backoff);

                try
                {
                    await Task.Delay(backoff, _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }
}
