using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Runtime;

public sealed class AcLoopSupervisorTests
{
    [Fact]
    public async Task Restarts_loop_after_failure()
    {
        var time = new FakeTimeProvider();
        await using var supervisor = new AcLoopSupervisor(time, NullLoggerFactory.Instance);
        var attempts = 0;
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        supervisor.Run("flaky", ct =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                throw new InvalidOperationException("first attempt fails");
            }

            restarted.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await RuntimeTestHelpers.WaitUntilAsync(() => Volatile.Read(ref attempts) >= 1);

        while (!restarted.Task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref attempts).ShouldBe(2);
    }

    [Fact]
    public async Task Stop_cancels_running_loops()
    {
        var time = new FakeTimeProvider();
        var supervisor = new AcLoopSupervisor(time, NullLoggerFactory.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        supervisor.Run("long", async ct =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromMinutes(10), time, ct);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await supervisor.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await supervisor.DisposeAsync();
    }
}
