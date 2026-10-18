using AiCtrl.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Logging;

public sealed class AcLogBootstrapTests
{
    [Fact]
    public void ReadOptions_reads_levels_and_limits_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["logging:fileLevel"] = "Warning",
                ["logging:consoleLevel"] = "Error",
                ["logging:retentionDays"] = "7",
                ["logging:maxTotalGB"] = "1.5",
            })
            .Build();

        var options = AcLogBootstrap.ReadOptions(configuration);

        options.FileLevel.ShouldBe(LogLevel.Warning);
        options.ConsoleLevel.ShouldBe(LogLevel.Error);
        options.RetentionDays.ShouldBe(7);
        options.MaxTotalGb.ShouldBe(1.5);
    }

    [Fact]
    public void ReadOptions_falls_back_to_defaults_when_missing_or_invalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["logging:fileLevel"] = "not-a-level",
                ["logging:maxTotalGB"] = "lots",
            })
            .Build();

        var options = AcLogBootstrap.ReadOptions(configuration);

        options.FileLevel.ShouldBe(LogLevel.Debug);
        options.ConsoleLevel.ShouldBe(LogLevel.Information);
        options.RetentionDays.ShouldBe(30);
        options.MaxTotalGb.ShouldBe(5);
    }
}
