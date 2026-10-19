using System.Text.Json;
using AiCtrl.ApiClient;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.ClientMCP;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Runtime;
using AiCtrl.Tests.Commands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Systems;

public sealed class LocalMcpToolsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private ScriptedProviderInstance _instance = null!;

    private StateSystem _state = null!;

    private SessionActorPool _actors = null!;

    private LocalApiSystem _api = null!;

    private LocalApiClient _client = null!;

    private LocalTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new ClientDb(Path.Combine(_directory, "client.db"));
        await db.InitializeAsync(ct);

        _actors = new SessionActorPool(NullLoggerFactory.Instance);
        _instance = new ScriptedProviderInstance();
        _instance.AddSession("ses_1", AcRunState.Idle);
        _instance.AddAssistantMessage("ses_1", completed: true);

        var registry = new FakeProviderInstanceRegistry(new MachineId("m_test"), new[] { _instance });
        var runner = new AbortAndPromptRunner(
            new AbortAndPromptOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(60)),
            TimeProvider.System,
            NullLogger<AbortAndPromptRunner>.Instance);

        var commands = new CommandSystem(
            new JournalStore(db),
            _actors,
            registry,
            runner,
            TimeProvider.System,
            NullLogger<CommandSystem>.Instance);

        _state = new StateSystem(TimeProvider.System, new MonitorOptions(), NullLoggerFactory.Instance);
        var context = new AcSystemContext(
            new ServiceCollection().BuildServiceProvider(),
            new ChannelAcEventBus(NullLoggerFactory.Instance),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new AcLoopSupervisor(TimeProvider.System, NullLoggerFactory.Instance));

        await _state.InitAsync(context, ct);

        _api = new LocalApiSystem(
            commands,
            registry,
            _state,
            new LocalApiOptions { Port = 0, TokenPath = Path.Combine(_directory, "local-token") },
            TimeProvider.System,
            NullLoggerFactory.Instance);

        await _api.InitAsync(context, ct);
        await _api.StartAsync(ct);

        _client = new LocalApiClient(new Uri($"http://127.0.0.1:{_api.Port}/"), _api.TokenPath);
        _tools = new LocalTools(_client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _api.StopAsync(CancellationToken.None);
        await _state.StopAsync(CancellationToken.None);
        await _actors.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Status_tool_returns_freshness_and_structured_content()
    {
        var result = await _tools.GetStatusAsync(TestContext.Current.CancellationToken);

        result.IsError.ShouldNotBe(true);
        TextOf(result).ShouldStartWith("[live ");
        result.StructuredContent.ShouldNotBeNull();
        result.StructuredContent!.Value.GetProperty("data").GetProperty("machineId").GetProperty("value").GetString().ShouldBe("m_test");
    }

    [Fact]
    public async Task Session_tools_read_through_local_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessions = await _tools.ListSessionsAsync(view: "all", ct: ct);

        sessions.IsError.ShouldNotBe(true);
        TextOf(sessions).ShouldContain("ses_1");
        sessions.StructuredContent!.Value.GetProperty("data").GetProperty("sessions").GetArrayLength().ShouldBe(1);

        var messages = await _tools.GetSessionMessagesAsync("ses_1", ct: ct);

        messages.IsError.ShouldNotBe(true);
        messages.StructuredContent!.Value.GetProperty("data").GetProperty("messages").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Side_effect_tool_without_confirm_only_previews()
    {
        var result = await _tools.AbortAndPromptAsync(
            "ses_1",
            "continue please",
            confirm: false,
            ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldNotBe(true);
        TextOf(result).ShouldContain("Preview only");
        result.StructuredContent.ShouldBeNull();
        _instance.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Side_effect_tool_with_confirm_executes_once()
    {
        var result = await _tools.AbortAndPromptAsync(
            "ses_1",
            "continue please",
            confirm: true,
            ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldNotBe(true);
        TextOf(result).ShouldContain("Succeeded");
        result.StructuredContent.ShouldNotBeNull();
        var data = result.StructuredContent!.Value.GetProperty("data");
        data.GetProperty("type").GetString().ShouldBe(CommandTypes.SessionAbortAndPrompt);
        data.GetProperty("status").GetString().ShouldBe("Succeeded");

        _instance.Prompts.Count.ShouldBe(1);
        _instance.Prompts[0].Text.ShouldBe("continue please");
    }

    private static string TextOf(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join(
            "\n",
            result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text));
}
