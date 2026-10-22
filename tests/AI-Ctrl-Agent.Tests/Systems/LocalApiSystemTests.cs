using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Workspace;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Runtime;
using AiCtrl.Core.Serialization;
using AiCtrl.Tests.Client;
using AiCtrl.Tests.Commands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Systems;

public sealed class LocalApiSystemTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private readonly string _tokenPath;

    private readonly string _workspaceRoot;

    private ScriptedProviderInstance _instance = null!;

    private StateSystem _state = null!;

    private SessionActorPool _actors = null!;

    private LocalApiSystem _api = null!;

    private HttpClient _client = null!;

    private HttpClient _anonymous = null!;

    public LocalApiSystemTests()
    {
        _tokenPath = Path.Combine(_directory, "local-token");
        _workspaceRoot = Path.Combine(_directory, "workspace");
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new ClientDb(Path.Combine(_directory, "client.db"));
        await db.InitializeAsync(ct);

        _actors = new SessionActorPool(NullLoggerFactory.Instance);
        _instance = new ScriptedProviderInstance();
        _instance.AddSession("ses_1", AcRunState.Busy);
        _instance.AddAssistantMessage("ses_1", completed: true);
        _instance.AddSession("ses_2", AcRunState.Idle);
        _instance.AddAssistantMessage("ses_2", completed: true);

        var registry = new FakeProviderInstanceRegistry(new MachineId("m_test"), new[] { _instance });
        var runner = new AbortAndPromptRunner(
            new AbortAndPromptOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(60)),
            TimeProvider.System,
            NullLogger<AbortAndPromptRunner>.Instance);

        Directory.CreateDirectory(_workspaceRoot);
        await File.WriteAllTextAsync(Path.Combine(_workspaceRoot, "note.txt"), "hello local api", ct);
        var workspace = new WorkspaceDefinition(
            "test",
            _workspaceRoot,
            AllowUpload: true,
            AllowDownload: true,
            AllowMkdir: true,
            AllowDelete: true,
            DenyPatterns: []);
        var workspaces = new WorkspaceRegistry(new WorkspaceOptions([workspace], []));
        var files = new FilesSystem(workspaces);
        var commands = new CommandSystem(
            new JournalStore(db),
            _actors,
            registry,
            runner,
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
            new LocalApiOptions { Port = 0, TokenPath = _tokenPath },
            TimeProvider.System,
            NullLoggerFactory.Instance);

        await _api.InitAsync(context, ct);
        await _api.StartAsync(ct);

        var baseUri = new Uri($"http://127.0.0.1:{_api.Port}/");
        var token = (await File.ReadAllTextAsync(_tokenPath, ct)).Trim();

        _client = new HttpClient { BaseAddress = baseUri };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _anonymous = new HttpClient { BaseAddress = baseUri };
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        _client.Dispose();
        _anonymous.Dispose();
        await _state.StopAsync(CancellationToken.None);
        await _actors.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Requires_local_token_and_valid_host()
    {
        var anonymous = await _anonymous.GetAsync("/local/v1/status", TestContext.Current.CancellationToken);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/local/v1/status");
        request.Headers.Host = "evil.example";
        var badHost = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        badHost.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var response = await _client.GetAsync("/local/v1/status", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        root.GetProperty("meta").GetProperty("source").GetString().ShouldBe("live");
        root.GetProperty("data").GetProperty("machineId").GetProperty("value").GetString().ShouldBe("m_test");
        root.GetProperty("data").GetProperty("localApiPort").GetInt32().ShouldBe(_api.Port);
        root.GetProperty("data").GetProperty("instanceCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Lists_sessions_with_projected_display_state()
    {
        var response = await _client.GetAsync(
            "/local/v1/instances/opencode:test/sessions?view=all",
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = await ReadJsonAsync(response);
        var sessions = document.RootElement.GetProperty("data").GetProperty("sessions");
        sessions.GetArrayLength().ShouldBe(2);

        var busy = sessions.EnumerateArray().Single(item => item.GetProperty("ref").GetProperty("sessionId").GetString() == "ses_1");
        busy.GetProperty("displayState").GetString().ShouldBe("Running");

        var idle = sessions.EnumerateArray().Single(item => item.GetProperty("ref").GetProperty("sessionId").GetString() == "ses_2");
        idle.GetProperty("displayState").GetString().ShouldBe("Idle");

        var all = await _client.GetAsync("/local/v1/sessions?view=all", TestContext.Current.CancellationToken);
        all.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var allDocument = await ReadJsonAsync(all);
        allDocument.RootElement.GetProperty("data").GetProperty("sessions").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Abort_and_prompt_runs_through_command_system()
    {
        using var content = new StringContent(
            "{\"text\":\"continue please\"}",
            Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync(
            "/local/v1/sessions/ses_2/abort-and-prompt",
            content,
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = await ReadJsonAsync(response);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("type").GetString().ShouldBe(CommandTypes.SessionAbortAndPrompt);
        data.GetProperty("status").GetString().ShouldBe("Succeeded");
        data.GetProperty("issuedBy").GetString().ShouldBe("local");

        var commandId = data.GetProperty("commandId").GetString().ShouldNotBeNull();
        var result = data.GetProperty("result");
        result.GetProperty("abortedSessions").GetInt32().ShouldBe(0);
        result.GetProperty("messageId").GetString().ShouldBe(commandId);

        _instance.Prompts.Count.ShouldBe(1);
        _instance.Prompts[0].Text.ShouldBe("continue please");

        var command = await _client.GetAsync(
            $"/local/v1/commands/{commandId}",
            TestContext.Current.CancellationToken);
        command.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var commandDocument = await ReadJsonAsync(command);
        commandDocument.RootElement.GetProperty("data").GetProperty("status").GetString().ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Missing_prompt_text_is_rejected()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(
            "/local/v1/sessions/ses_2/prompt",
            content,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var document = await ReadJsonAsync(response);
        document.RootElement.GetProperty("code").GetString().ShouldBe("invalid_payload");
    }

    [Fact]
    public async Task Files_endpoints_list_stat_preview_and_mutate()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspaceId = await GetWorkspaceIdAsync(ct);

        var list = await _client.GetAsync(
            $"/local/v1/files?workspaceId={workspaceId}&sort=name&limit=50",
            ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var listDocument = await ReadJsonAsync(list);
        var entries = listDocument.RootElement.GetProperty("data").GetProperty("entries");
        entries.EnumerateArray().ShouldContain(item => item.GetProperty("name").GetString() == "note.txt");

        var stat = await _client.GetAsync(
            $"/local/v1/files/stat?workspaceId={workspaceId}&path=note.txt&hash=true",
            ct);
        stat.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var statDocument = await ReadJsonAsync(stat);
        var statData = statDocument.RootElement.GetProperty("data");
        statData.GetProperty("size").GetInt64().ShouldBe(15);
        statData.GetProperty("sha256").GetString()!.Length.ShouldBe(64);

        var preview = await _client.GetAsync(
            $"/local/v1/files/preview?workspaceId={workspaceId}&path=note.txt",
            ct);
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var previewDocument = await ReadJsonAsync(preview);
        var previewData = previewDocument.RootElement.GetProperty("data");
        previewData.GetProperty("kind").GetString().ShouldBe("text");
        previewData.GetProperty("text").GetString().ShouldBe("hello local api");

        using var mkdirContent = new StringContent(
            JsonSerializer.Serialize(
                new FilesMkdirPayload(workspaceId, "docs"),
                AcJsonContext.Default.FilesMkdirPayload),
            Encoding.UTF8,
            "application/json");
        var mkdir = await _client.PostAsync("/local/v1/files/mkdir", mkdirContent, ct);
        mkdir.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(Path.Combine(_workspaceRoot, "docs")).ShouldBeTrue();

        using var renameContent = new StringContent(
            JsonSerializer.Serialize(
                new FilesRenamePayload(workspaceId, "docs", "docs2"),
                AcJsonContext.Default.FilesRenamePayload),
            Encoding.UTF8,
            "application/json");
        var rename = await _client.PostAsync("/local/v1/files/rename", renameContent, ct);
        rename.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(Path.Combine(_workspaceRoot, "docs2")).ShouldBeTrue();

        using var deleteContent = new StringContent(
            JsonSerializer.Serialize(
                new FilesDeletePayload(workspaceId, "docs2"),
                AcJsonContext.Default.FilesDeletePayload),
            Encoding.UTF8,
            "application/json");
        var delete = await _client.PostAsync("/local/v1/files/delete", deleteContent, ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(Path.Combine(_workspaceRoot, "docs2")).ShouldBeFalse();
    }

    [Fact]
    public async Task Files_endpoints_reject_traversal_and_unknown_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspaceId = await GetWorkspaceIdAsync(ct);

        var traversal = await _client.GetAsync(
            $"/local/v1/files?workspaceId={workspaceId}&path=..%2F..%2Fsecret",
            ct);
        traversal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var traversalDocument = await ReadJsonAsync(traversal);
        traversalDocument.RootElement.GetProperty("code").GetString().ShouldBe("path_invalid");

        var unknown = await _client.GetAsync("/local/v1/files?workspaceId=ws_missing", ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var unknownDocument = await ReadJsonAsync(unknown);
        unknownDocument.RootElement.GetProperty("code").GetString().ShouldBe("workspace_not_allowed");
    }

    private async Task<string> GetWorkspaceIdAsync(CancellationToken ct)
    {
        var response = await _client.GetAsync("/local/v1/workspaces", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        return document.RootElement
            .GetProperty("data")[0]
            .GetProperty("workspaceId")
            .GetString()
            .ShouldNotBeNull();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(json);
    }
}
