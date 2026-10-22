using System.Text.Json;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Api;
using AiCtrl.Tests.Client;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Serialization;
using AiCtrl.Tests.Systems;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Commands;

public sealed class CommandSystemTests : IAsyncLifetime
{
    private static readonly SessionRef Session = new(new MachineId("m_test"), new InstanceId("opencode:test"), "ses_1");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private SessionActorPool _actors = null!;

    private ScriptedProviderInstance _instance = null!;

    private CommandSystem _commands = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new ClientDb(Path.Combine(_directory, "client.db"));
        await db.InitializeAsync(ct);

        _actors = new SessionActorPool(NullLoggerFactory.Instance);
        _instance = new ScriptedProviderInstance();
        var registry = new FakeProviderInstanceRegistry(new MachineId("m_test"), new[] { _instance });
        var runner = new AbortAndPromptRunner(
            new AbortAndPromptOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(60)),
            TimeProvider.System,
            NullLogger<AbortAndPromptRunner>.Instance);

        _commands = new CommandSystem(
            new JournalStore(db),
            _actors,
            registry,
            runner,
            new FilesSystem(new WorkspaceRegistry(new WorkspaceOptions([], []))),
            TestTransferSystem.Create(db, new WorkspaceRegistry(new WorkspaceOptions([], []))),
            TimeProvider.System,
            NullLogger<CommandSystem>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _actors.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Same_command_id_is_executed_once()
    {
        var ct = TestContext.Current.CancellationToken;
        _instance.AddSession("ses_1", AcRunState.Idle);
        var envelope = PromptEnvelope("cmd_dup");

        var first = await _commands.ExecuteAsync(envelope, ct);
        var second = await _commands.ExecuteAsync(envelope, ct);

        first.Succeeded.ShouldBeTrue();
        second.Status.ShouldBe(CommandStatus.Succeeded);
        _instance.Prompts.Count.ShouldBe(1);

        var view = await _commands.GetViewAsync("cmd_dup", ct);
        view.ShouldNotBeNull();
        view.Status.ShouldBe(CommandStatus.Succeeded);
    }

    [Fact]
    public async Task Concurrent_duplicate_command_waits_for_single_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        _instance.AddSession("ses_1", AcRunState.Idle);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _instance.PromptGate = gate;
        var envelope = PromptEnvelope("cmd_concurrent");

        var first = _commands.ExecuteAsync(envelope, ct);
        await _instance.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var second = _commands.ExecuteAsync(envelope, ct);
        await Task.Delay(50, ct);

        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        results[0].Succeeded.ShouldBeTrue();
        results[1].Status.ShouldBe(CommandStatus.Succeeded);
        _instance.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unconfirmed_abort_and_prompt_is_journaled_unknown_without_resend()
    {
        var ct = TestContext.Current.CancellationToken;
        _instance.AddSession("ses_1", AcRunState.Idle);
        _instance.AcceptPrompt = _ => false;
        var envelope = AbortAndPromptEnvelope("cmd_unknown");

        var first = await _commands.ExecuteAsync(envelope, ct);

        first.Status.ShouldBe(CommandStatus.Unknown);
        first.ErrorCode.ShouldBe(AcErrorCodes.PromptUnconfirmed);

        var second = await _commands.ExecuteAsync(envelope, ct);

        second.Status.ShouldBe(CommandStatus.Unknown);
        _instance.Prompts.Count.ShouldBe(1);

        var view = await _commands.GetViewAsync("cmd_unknown", ct);
        view.ShouldNotBeNull();
        view.Status.ShouldBe(CommandStatus.Unknown);
        view.ErrorCode.ShouldBe(AcErrorCodes.PromptUnconfirmed);
    }

    [Fact]
    public async Task Unsupported_command_fails_and_is_journaled()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = new CommandEnvelope(
            "cmd_unsupported",
            new MachineId("m_test"),
            "vpn.connect",
            JsonSerializer.SerializeToElement(new { }),
            "local",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            0);

        var result = await _commands.ExecuteAsync(envelope, ct);

        result.Status.ShouldBe(CommandStatus.Failed);
        result.ErrorCode.ShouldBe(AcErrorCodes.UnsupportedCommand);

        var view = await _commands.GetViewAsync("cmd_unsupported", ct);
        view.ShouldNotBeNull();
        view.Status.ShouldBe(CommandStatus.Failed);
    }

    private static CommandEnvelope PromptEnvelope(string commandId) =>
        new(
            commandId,
            new MachineId("m_test"),
            CommandTypes.SessionPrompt,
            JsonSerializer.SerializeToElement(new SessionPromptPayload(Session, "hello", null, null), AcJsonContext.Default.SessionPromptPayload),
            "local",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            0);

    private static CommandEnvelope AbortAndPromptEnvelope(string commandId) =>
        new(
            commandId,
            new MachineId("m_test"),
            CommandTypes.SessionAbortAndPrompt,
            JsonSerializer.SerializeToElement(new SessionAbortAndPromptPayload(Session, "hello", null), AcJsonContext.Default.SessionAbortAndPromptPayload),
            "local",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            0);
}
