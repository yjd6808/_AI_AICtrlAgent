using System.Diagnostics;
using System.Text.Json;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Workspace;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Runtime;
using AiCtrl.Tests.Client;
using AiCtrl.Tests.Commands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Systems;

public sealed class LocalMcpStdioTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private ScriptedProviderInstance _instance = null!;

    private StateSystem _state = null!;

    private SessionActorPool _actors = null!;

    private LocalApiSystem _api = null!;

    private Process? _process;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new ClientDb(Path.Combine(_directory, "client.db"));
        await db.InitializeAsync(ct);

        _actors = new SessionActorPool(NullLoggerFactory.Instance);
        _instance = new ScriptedProviderInstance();
        _instance.AddSession("ses_1", AcRunState.Idle);
        _instance.AddAssistantMessage("ses_1", completed: true);

        var registry = new FakeProviderInstanceRegistry(new MachineId("m_stdio"), new[] { _instance });
        var workspaces = new WorkspaceRegistry(new WorkspaceOptions([], []));
        var files = new FilesSystem(workspaces);
        var commands = new CommandSystem(
            new JournalStore(db),
            _actors,
            registry,
            new AbortAndPromptRunner(
                new AbortAndPromptOptions(
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(60)),
                TimeProvider.System,
                NullLogger<AbortAndPromptRunner>.Instance),
            files,
            TestTransferSystem.Create(db, workspaces),
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
            files,
            new LocalApiOptions { Port = 0, TokenPath = Path.Combine(_directory, "local-token") },
            TimeProvider.System,
            NullLoggerFactory.Instance);

        await _api.InitAsync(context, ct);
        await _api.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { } process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            process.Dispose();
        }

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
    public async Task Stdio_client_can_abort_and_prompt_without_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var executable = Path.Combine(AppContext.BaseDirectory, "AI-Ctrl-Agent.ClientMCP.exe");
        File.Exists(executable).ShouldBeTrue();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["AICTRL_LOCAL_PORT"] = _api.Port.ToString();
        startInfo.Environment["AICTRL_LOCAL_TOKEN"] = _api.TokenPath;

        _process = Process.Start(startInfo);
        _process.ShouldNotBeNull();
        using var input = _process!.StandardInput;
        using var output = _process.StandardOutput;

        using var init = await SendAsync(
            input,
            output,
            1,
            "initialize",
            new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "tests", version = "1.0" } },
            ct);
        init.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldNotBeNullOrEmpty();

        await input.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await input.FlushAsync(ct);

        using var call = await SendAsync(
            input,
            output,
            2,
            "tools/call",
            new
            {
                name = "local_abort_and_prompt",
                arguments = new { sessionId = "ses_1", text = "continue from MCP", confirm = true },
            },
            ct);

        var result = call.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("structuredContent").GetProperty("data").GetProperty("status").GetString().ShouldBe("Succeeded");
        _instance.Prompts.Count.ShouldBe(1);
        _instance.Prompts[0].Text.ShouldBe("continue from MCP");
    }

    private static async Task<JsonDocument> SendAsync(
        StreamWriter input,
        StreamReader output,
        int id,
        string method,
        object parameters,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await input.WriteLineAsync(payload).ConfigureAwait(false);
        await input.FlushAsync(ct).ConfigureAwait(false);

        while (true)
        {
            var line = await output.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException("MCP process closed stdout before responding.");
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == id)
            {
                return document;
            }

            document.Dispose();
        }
    }
}
