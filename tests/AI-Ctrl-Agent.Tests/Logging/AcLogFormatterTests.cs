using AiCtrl.Core.Logging;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace AiCtrl.Tests.Logging;

public sealed class AcLogFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 9, 0, 0, 123, TimeSpan.FromHours(9));

    [Fact]
    public void ComposeLine_has_expected_shape()
    {
        var line = AcLogFormatter.ComposeLine(Timestamp, LogLevel.Information, 12, "Cat.Sub", "hello", string.Empty, null);

        line.ShouldBe("2026-01-01 09:00:00.123 +09:00 [INF] [T12] [Cat.Sub] hello");
    }

    [Fact]
    public void ComposeLine_includes_scope_suffix()
    {
        var line = AcLogFormatter.ComposeLine(Timestamp, LogLevel.Debug, 7, "Cat", "msg", "{trace=abc}", null);

        line.ShouldBe("2026-01-01 09:00:00.123 +09:00 [DBG] [T07] [Cat] msg {trace=abc}");
    }

    [Fact]
    public void ComposeLine_indents_exception_lines()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var line = AcLogFormatter.ComposeLine(Timestamp, LogLevel.Error, 3, "Cat", "failed", string.Empty, exception);

        line.ShouldContain("\n    System.InvalidOperationException: boom");
        line.ShouldContain("\n       at ");
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRC")]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Information, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    [InlineData(LogLevel.Critical, "FTL")]
    public void LevelTag_maps_levels(LogLevel level, string expected) =>
        AcLogFormatter.LevelTag(level).ShouldBe(expected);
}
