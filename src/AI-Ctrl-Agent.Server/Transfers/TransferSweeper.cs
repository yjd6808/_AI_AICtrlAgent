using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Server.Transfers;

public sealed class TransferSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly TransferStore _transfers;

    private readonly TimeProvider _time;

    private readonly ILogger<TransferSweeper> _logger;

    public TransferSweeper(
        TransferStore transfers,
        TimeProvider time,
        ILogger<TransferSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _transfers = transfers;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
                await _transfers.CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transfer cleanup failed.");
            }
        }
    }
}
