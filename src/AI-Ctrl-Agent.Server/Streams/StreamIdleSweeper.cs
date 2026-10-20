using Microsoft.Extensions.Hosting;

namespace AiCtrl.Server.Streams;

public sealed class StreamIdleSweeper : BackgroundService
{
    private readonly SessionStreamHub _streams;

    private readonly TimeProvider _time;

    public StreamIdleSweeper(SessionStreamHub streams, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(time);
        _streams = streams;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _streams.SweepIdle();
        }
    }
}
