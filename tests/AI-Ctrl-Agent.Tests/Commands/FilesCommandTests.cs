using System.Text.Json;
using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Api;
using AiCtrl.Core.Config;
using AiCtrl.Tests.Client;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using AiCtrl.Tests.Systems;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AiCtrl.Tests.Commands;

public sealed class FilesCommandTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aictrl-tests", Guid.NewGuid().ToString("N"));

    private SessionActorPool _actors = null!;

    private CommandSystem _commands = null!;

    private string _workspaceId = null!;

    private string _overridesPath = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_directory, "workspace"));
        var db = new ClientDb(Path.Combine(_directory, "client.db"));
        await db.InitializeAsync(ct);

        var workspace = new WorkspaceDefinition(
            "test",
            Path.Combine(_directory, "workspace"),
            AllowUpload: true,
            AllowDownload: true,
            AllowMkdir: true,
            AllowDelete: true,
            DenyPatterns: []);
        var registry = new WorkspaceRegistry(new WorkspaceOptions([workspace], [_directory]));
        _workspaceId = registry.Entries[0].WorkspaceId;
        _overridesPath = Path.Combine(_directory, "client.overrides.yaml");

        _actors = new SessionActorPool(NullLoggerFactory.Instance);
        var providerRegistry = new FakeProviderInstanceRegistry(new MachineId("m_test"), []);
        _commands = new CommandSystem(
            new JournalStore(db),
            _actors,
            providerRegistry,
            new AbortAndPromptRunner(
                new AbortAndPromptOptions(
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(60)),
                TimeProvider.System,
                NullLogger<AbortAndPromptRunner>.Instance),
            new FilesSystem(registry),
            TestTransferSystem.Create(db, registry),
            TimeProvider.System,
            NullLogger<CommandSystem>.Instance,
            new WorkspaceRegistrationStore(_overridesPath));
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
    public async Task File_commands_create_rename_and_delete()
    {
        var ct = TestContext.Current.CancellationToken;

        var mkdir = await _commands.ExecuteAsync(
            Envelope(
                "cmd_mkdir",
                CommandTypes.FilesMkdir,
                JsonSerializer.SerializeToElement(
                    new FilesMkdirPayload(_workspaceId, "docs"),
                    AcJsonContext.Default.FilesMkdirPayload)),
            ct);
        mkdir.Succeeded.ShouldBeTrue();
        var created = mkdir.Result!.Value.Deserialize(AcJsonContext.Default.FileStatView).ShouldNotBeNull();
        created.IsDir.ShouldBeTrue();
        created.RelPath.ShouldBe("docs");

        var rename = await _commands.ExecuteAsync(
            Envelope(
                "cmd_rename",
                CommandTypes.FilesRename,
                JsonSerializer.SerializeToElement(
                    new FilesRenamePayload(_workspaceId, "docs", "docs2"),
                    AcJsonContext.Default.FilesRenamePayload)),
            ct);
        rename.Succeeded.ShouldBeTrue();
        Directory.Exists(Path.Combine(_directory, "workspace", "docs2")).ShouldBeTrue();

        var delete = await _commands.ExecuteAsync(
            Envelope(
                "cmd_delete",
                CommandTypes.FilesDelete,
                JsonSerializer.SerializeToElement(
                    new FilesDeletePayload(_workspaceId, "docs2"),
                    AcJsonContext.Default.FilesDeletePayload)),
            ct);
        delete.Succeeded.ShouldBeTrue();
        Directory.Exists(Path.Combine(_directory, "workspace", "docs2")).ShouldBeFalse();
    }

    [Fact]
    public async Task File_command_rejects_path_escape()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _commands.ExecuteAsync(
            Envelope(
                "cmd_escape",
                CommandTypes.FilesMkdir,
                JsonSerializer.SerializeToElement(
                    new FilesMkdirPayload(_workspaceId, "../escape"),
                    AcJsonContext.Default.FilesMkdirPayload)),
            ct);

        result.Status.ShouldBe(CommandStatus.Failed);
        result.ErrorCode.ShouldBe(AcErrorCodes.PathInvalid);
    }

    [Fact]
    public async Task Workspace_register_adds_workspace_and_persists_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(_directory, "new-workspace");
        Directory.CreateDirectory(root);

        var result = await _commands.ExecuteAsync(
            Envelope(
                "cmd_register",
                CommandTypes.WorkspaceRegister,
                JsonSerializer.SerializeToElement(
                    new WorkspaceRegisterPayload(root),
                    AcJsonContext.Default.WorkspaceRegisterPayload)),
            ct);

        result.Status.ShouldBe(CommandStatus.Succeeded);
        result.Result!.Value.GetProperty("root").GetString().ShouldBe(root);

        File.Exists(_overridesPath).ShouldBeTrue();
        var configuration = new ConfigurationBuilder()
            .AddAcYamlFile(_overridesPath)
            .Build();
        var reloaded = WorkspaceOptions.FromConfiguration(configuration);
        reloaded.Workspaces.ShouldContain(workspace => workspace.Root == root);
    }

    [Fact]
    public async Task Workspace_register_rejects_path_outside_parents()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(Path.GetTempPath(), "aictrl-tests-outside", Guid.NewGuid().ToString("N"));

        var result = await _commands.ExecuteAsync(
            Envelope(
                "cmd_register_outside",
                CommandTypes.WorkspaceRegister,
                JsonSerializer.SerializeToElement(
                    new WorkspaceRegisterPayload(outside),
                    AcJsonContext.Default.WorkspaceRegisterPayload)),
            ct);

        result.Status.ShouldBe(CommandStatus.Failed);
        result.ErrorCode.ShouldBe(AcErrorCodes.WorkspaceNotAllowed);
        File.Exists(_overridesPath).ShouldBeFalse();
    }

    private static CommandEnvelope Envelope(string commandId, string type, JsonElement payload) =>
        new(
            commandId,
            new MachineId("m_test"),
            type,
            payload,
            "local",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            0);
}
