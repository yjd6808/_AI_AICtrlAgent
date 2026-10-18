using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Runtime;

public sealed class AcAppTests
{
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class RecordingSystem : IAcSystem
    {
        private readonly List<string> _log;
        private int _initAttempts;

        public RecordingSystem(string name, IReadOnlyList<string> dependsOn, List<string> log, int failInitTimes = 0)
        {
            Name = name;
            DependsOn = dependsOn;
            _log = log;
            FailInitTimes = failInitTimes;
        }

        public string Name { get; }

        public IReadOnlyList<string> DependsOn { get; }

        public int FailInitTimes { get; }

        public int InitAttempts => Volatile.Read(ref _initAttempts);

        public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _initAttempts);
            _log.Add($"init:{Name}");

            if (attempt <= FailInitTimes)
            {
                throw new InvalidOperationException($"init failure {attempt} for {Name}");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(CancellationToken ct)
        {
            _log.Add($"start:{Name}");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken ct)
        {
            _log.Add($"stop:{Name}");
            return ValueTask.CompletedTask;
        }
    }

    private static AcApp CreateApp(IEnumerable<IAcSystem> systems, Action<int>? fatalExit = null) =>
        new(
            new StubServiceProvider(),
            systems,
            new ChannelAcEventBus(NullLoggerFactory.Instance),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            fatalExit,
            TimeSpan.Zero);

    [Fact]
    public async Task Start_initializes_dependencies_before_dependents()
    {
        var log = new List<string>();
        var systemA = new RecordingSystem("A", [], log);
        var systemB = new RecordingSystem("B", ["A"], log);
        await using var app = CreateApp([systemB, systemA]);

        await app.StartAsync(CancellationToken.None);

        log.ShouldBe(["init:A", "start:A", "init:B", "start:B"]);
    }

    [Fact]
    public async Task Start_throws_on_dependency_cycle()
    {
        var log = new List<string>();
        var systemA = new RecordingSystem("A", ["B"], log);
        var systemB = new RecordingSystem("B", ["A"], log);
        await using var app = CreateApp([systemA, systemB]);

        await Should.ThrowAsync<ApplicationException>(app.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Start_throws_on_unknown_dependency()
    {
        var log = new List<string>();
        var systemA = new RecordingSystem("A", ["missing"], log);
        await using var app = CreateApp([systemA]);

        await Should.ThrowAsync<ApplicationException>(app.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stop_stops_systems_in_reverse_order()
    {
        var log = new List<string>();
        var systemA = new RecordingSystem("A", [], log);
        var systemB = new RecordingSystem("B", ["A"], log);
        await using var app = CreateApp([systemA, systemB]);

        await app.StartAsync(CancellationToken.None);
        await app.StopAsync(CancellationToken.None);

        log.ShouldBe(["init:A", "start:A", "init:B", "start:B", "stop:B", "stop:A"]);
    }

    [Fact]
    public async Task Init_retries_until_success()
    {
        var log = new List<string>();
        var fatalExitCode = -1;
        var system = new RecordingSystem("Flaky", [], log, failInitTimes: 1);
        await using var app = CreateApp([system], code => fatalExitCode = code);

        await app.StartAsync(CancellationToken.None);

        system.InitAttempts.ShouldBe(2);
        fatalExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Init_failure_three_times_calls_fatal_exit_with_code_3()
    {
        var log = new List<string>();
        var fatalExitCode = -1;
        var system = new RecordingSystem("Broken", [], log, failInitTimes: 3);
        await using var app = CreateApp([system], code => fatalExitCode = code);

        await Should.ThrowAsync<InvalidOperationException>(app.StartAsync(CancellationToken.None));

        system.InitAttempts.ShouldBe(3);
        fatalExitCode.ShouldBe(3);
    }
}
