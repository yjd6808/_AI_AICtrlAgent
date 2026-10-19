using System.Threading.Channels;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Commands;

public sealed class SessionActor : IAsyncDisposable
{
    private readonly Channel<WorkItem> _queue;
    private readonly SemaphoreSlim _priorityGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ILogger _logger;

    public SessionActor(SessionRef session, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Session = session;
        _logger = logger;
        _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _loop = Task.Run(RunAsync);
    }

    public SessionRef Session { get; }

    public ValueTask<CommandResult> ExecuteAsync(
        string type,
        Func<CancellationToken, ValueTask<CommandResult>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (type == CommandTypes.SessionAbort)
        {
            return ExecutePriorityAsync(work, ct);
        }

        var item = new WorkItem(work, new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        return EnqueueAsync(item, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _priorityGate.Dispose();
        _cts.Dispose();
    }

    private async ValueTask<CommandResult> EnqueueAsync(WorkItem item, CancellationToken ct)
    {
        await _queue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        return await item.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<CommandResult> ExecutePriorityAsync(
        Func<CancellationToken, ValueTask<CommandResult>> work,
        CancellationToken ct)
    {
        await _priorityGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _priorityGate.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    var result = await item.Work(_cts.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(_cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session actor work failed for {Session}", Session);
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record WorkItem(
        Func<CancellationToken, ValueTask<CommandResult>> Work,
        TaskCompletionSource<CommandResult> Completion);
}
