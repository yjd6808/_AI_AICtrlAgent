using System.Text;
using AiCtrl.Core.Config;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Config;

public sealed class AcYamlWatchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aictrl-watch-" + Guid.NewGuid().ToString("N"));

    public AcYamlWatchTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string YamlPath => Path.Combine(_directory, "watch.yaml");

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    [Fact]
    public async Task Reloads_when_file_changes()
    {
        File.WriteAllText(YamlPath, "logging:\n  fileLevel: Debug\n");
        using var provider = new AcYamlConfigurationProvider(
            YamlPath,
            optional: false,
            watch: true,
            reloadDebounce: TimeSpan.FromMilliseconds(50));
        provider.Load();
        provider.TryGet("logging:fileLevel", out var initial).ShouldBeTrue();
        initial.ShouldBe("Debug");

        File.WriteAllText(YamlPath, "logging:\n  fileLevel: Trace\n");

        var reloaded = await WaitUntilAsync(() =>
            provider.TryGet("logging:fileLevel", out var value) && value == "Trace");

        reloaded.ShouldBeTrue();
        provider.LastReloadError.ShouldBeNull();
    }

    [Fact]
    public async Task Keeps_previous_values_when_change_is_invalid()
    {
        File.WriteAllText(YamlPath, "logging:\n  fileLevel: Debug\n");
        using var provider = new AcYamlConfigurationProvider(
            YamlPath,
            optional: false,
            watch: true,
            reloadDebounce: TimeSpan.FromMilliseconds(50));
        provider.Load();

        File.WriteAllText(YamlPath, "a: 1\na: 2\n");

        var failed = await WaitUntilAsync(() => provider.LastReloadError is not null);

        failed.ShouldBeTrue();
        provider.TryGet("logging:fileLevel", out var value).ShouldBeTrue();
        value.ShouldBe("Debug");
    }

    [Fact]
    public async Task Identical_content_does_not_trigger_reload()
    {
        const string content = "logging:\n  fileLevel: Debug\n";
        File.WriteAllText(YamlPath, content);
        using var provider = new AcYamlConfigurationProvider(
            YamlPath,
            optional: false,
            watch: true,
            reloadDebounce: TimeSpan.FromMilliseconds(50));
        provider.Load();
        provider.ContentHash.ShouldBe(AcYamlConfigurationProvider.ComputeHash(content));

        var reloadCount = 0;
        provider.GetReloadToken().RegisterChangeCallback(_ => Interlocked.Increment(ref reloadCount), null);

        File.WriteAllText(YamlPath, content);
        await Task.Delay(700, TestContext.Current.CancellationToken);

        reloadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Created_event_loads_file_that_appeared_later()
    {
        using var provider = new AcYamlConfigurationProvider(
            YamlPath,
            optional: true,
            watch: true,
            reloadDebounce: TimeSpan.FromMilliseconds(50));
        provider.Load();
        provider.TryGet("logging:fileLevel", out _).ShouldBeFalse();

        File.WriteAllText(YamlPath, "logging:\n  fileLevel: Warning\n");

        var loaded = await WaitUntilAsync(() =>
            provider.TryGet("logging:fileLevel", out var value) && value == "Warning");

        loaded.ShouldBeTrue();
    }
}
