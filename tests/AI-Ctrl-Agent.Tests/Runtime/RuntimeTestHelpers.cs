using System.Diagnostics;

namespace AiCtrl.Tests.Runtime;

internal static class RuntimeTestHelpers
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.Elapsed > limit)
            {
                throw new TimeoutException("Condition was not met within the time limit.");
            }

            await Task.Delay(10);
        }
    }
}
