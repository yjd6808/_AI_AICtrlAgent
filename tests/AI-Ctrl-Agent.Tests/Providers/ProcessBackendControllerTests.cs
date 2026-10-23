using AiCtrl.Client.Providers.OpenCode.Backend;
using Shouldly;

namespace AiCtrl.Tests.Providers;

public sealed class ProcessBackendControllerTests
{
    [Fact]
    public async Task Starts_cmd_shim_through_command_interpreter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var outputPath = Path.Combine(directory, "arguments.txt");
            var shimPath = Path.Combine(directory, "shim.cmd");
            await File.WriteAllLinesAsync(
                shimPath,
                new[]
                {
                    "@echo off",
                    "> \"%~1\" echo %~2 %~3",
                },
                TestContext.Current.CancellationToken);

            var controller = new ProcessBackendController(
                shimPath,
                new[] { outputPath, "one two", "three" },
                new Dictionary<string, string>());

            await using var process = await controller.StartAsync(TestContext.Current.CancellationToken);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(outputPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            File.Exists(outputPath).ShouldBeTrue();
            (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim()
                .ShouldBe("one two three");
            await process.KillAsync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
