using AiCtrl.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Logging;

public sealed class AcLogServiceTests
{
    private static FakeTimeProvider CreateTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Initialize_creates_log_file_with_start_header()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AcLogService("AiCtrl.Test", new AcLogOptions { Directory = directory }, CreateTime());

            File.Exists(service.FilePath).ShouldBeTrue();
            Path.GetFileName(service.FilePath).ShouldBe("20260101-090000-AiCtrl.Test.txt");

            var content = ReadAllTextShared(service.FilePath);
            content.ShouldContain("==== START AiCtrl.Test");
            content.ShouldContain("==== args:");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Write_flushes_to_file_and_shutdown_writes_stop()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new AcLogService("AiCtrl.Test", new AcLogOptions { Directory = directory }, CreateTime());
            service.Write(LogLevel.Information, "Cat.Sub", "hello world");
            service.Shutdown(0);

            var content = ReadAllTextShared(service.FilePath);
            content.ShouldContain("[INF]");
            content.ShouldContain("[Cat.Sub]");
            content.ShouldContain("hello world");
            content.ShouldContain("==== STOP exitCode=0");
            content.ShouldContain("dropped=0");
            content.ShouldContain("\r\n");

            service.Dispose();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void File_is_written_without_bom()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new AcLogService("AiCtrl.Test", new AcLogOptions { Directory = directory }, CreateTime());
            service.Shutdown(0);

            var bytes = File.ReadAllBytes(service.FilePath);
            bytes[0].ShouldNotBe((byte)0xEF);
            service.Dispose();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Redacts_secrets_in_logged_lines()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new AcLogService("AiCtrl.Test", new AcLogOptions { Directory = directory }, CreateTime());
            service.Write(LogLevel.Information, "Cat", "Authorization: Bearer sekret123");
            service.Shutdown(0);

            var content = ReadAllTextShared(service.FilePath);
            content.ShouldContain("Authorization: ****");
            content.ShouldNotContain("sekret123");

            service.Dispose();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Rotates_part_files_when_size_exceeded()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new AcLogService("AiCtrl.Test", new AcLogOptions { Directory = directory, MaxFileBytes = 512 }, CreateTime());
            for (var i = 0; i < 40; i++)
            {
                service.Write(LogLevel.Information, "Cat", $"payload-{i:D3} " + new string('x', 50));
            }

            service.Shutdown(0);

            var files = Directory.GetFiles(directory, "*.txt");
            files.Length.ShouldBeGreaterThan(1);
            File.Exists(Path.Combine(directory, "20260101-090000-AiCtrl.Test.part2.txt")).ShouldBeTrue();

            var total = files.Sum(f => ReadAllTextShared(f).Split("payload-").Length - 1);
            total.ShouldBe(40);

            service.Dispose();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Retention_deletes_files_older_than_configured_days()
    {
        var directory = CreateTempDirectory();
        try
        {
            var oldPath = Path.Combine(directory, "20200101-000000-OldApp.txt");
            File.WriteAllText(oldPath, "old");
            File.SetLastWriteTimeUtc(oldPath, new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc));

            using var service = new AcLogService(
                "AiCtrl.Test",
                new AcLogOptions { Directory = directory, RetentionDays = 30 },
                CreateTime());

            File.Exists(oldPath).ShouldBeFalse();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Retention_deletes_oldest_when_total_size_exceeds_limit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var older = Path.Combine(directory, "20250101-000000-A.txt");
            var newer = Path.Combine(directory, "20250102-000000-B.txt");
            File.WriteAllText(older, new string('a', 1000));
            File.WriteAllText(newer, new string('b', 1000));
            File.SetLastWriteTimeUtc(older, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var maxGb = 1500.0 / (1024 * 1024 * 1024);
            using var service = new AcLogService(
                "AiCtrl.Test",
                new AcLogOptions { Directory = directory, RetentionDays = 3650, MaxTotalGb = maxGb },
                CreateTime());

            File.Exists(older).ShouldBeFalse();
            File.Exists(newer).ShouldBeTrue();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Second_service_in_same_second_uses_pid_suffix()
    {
        var directory = CreateTempDirectory();
        try
        {
            var options = new AcLogOptions { Directory = directory };
            using var first = new AcLogService("AiCtrl.Test", options, CreateTime());
            using var second = new AcLogService("AiCtrl.Test", options, CreateTime());

            Path.GetFileName(first.FilePath).ShouldBe("20260101-090000-AiCtrl.Test.txt");
            Path.GetFileName(second.FilePath).ShouldBe($"20260101-090000-AiCtrl.Test-{Environment.ProcessId}.txt");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Logger_factory_writes_messages_and_scopes()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new AcLogService("AiCtrl.Test", new AcLogOptions { Directory = directory }, CreateTime());

            using (var factory = new LoggerFactory([service.CreateProvider()]))
            {
                var logger = factory.CreateLogger("Unit.Cat");
                using (logger.BeginScope(new Dictionary<string, object> { ["trace"] = "4bf9", ["cmd"] = "01JB" }))
                {
                    logger.LogInformation("hello {Name}", "world");
                }

                logger.LogWarning("warn now");
            }

            service.Shutdown(0);

            var content = ReadAllTextShared(service.FilePath);
            content.ShouldContain("hello world");
            content.ShouldContain("[Unit.Cat]");
            content.ShouldContain("{trace=4bf9 cmd=01JB}");
            content.ShouldContain("[WRN]");
            content.ShouldContain("warn now");

            service.Dispose();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }
}
