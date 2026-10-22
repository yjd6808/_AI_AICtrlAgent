using System.Text;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Workspace;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace AiCtrl.Tests.Workspace;

public sealed class FilesSystemTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "aictrl-files", Guid.NewGuid().ToString("N"));

    private readonly string _root;

    private readonly string _workspaceId;

    private readonly WorkspaceRegistry _registry;

    private readonly FilesSystem _files;

    public FilesSystemTests()
    {
        _root = Path.Combine(_parent, "workspace");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "docs", "note.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "docs", ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_root, "root.txt"), "root");
        _registry = new WorkspaceRegistry(new WorkspaceOptions(
            [new WorkspaceDefinition("project", _root, true, true, true, true, [])],
            [_parent]));
        _workspaceId = _registry.Entries[0].WorkspaceId;
        _files = new FilesSystem(_registry);
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, recursive: true);
        }
    }

    [Fact]
    public void Generates_stable_workspace_ids()
    {
        var first = WorkspaceRegistry.CreateWorkspaceId(_root);
        var second = WorkspaceRegistry.CreateWorkspaceId(_root.ToUpperInvariant());

        first.ShouldBe(second);
        first.ShouldStartWith("ws_");
        first.Length.ShouldBe(11);
    }

    [Fact]
    public void Unknown_workspace_is_rejected()
    {
        var exception = Should.Throw<WorkspaceException>(() =>
            _files.List(new FileListQuery("ws_missing", null, FileSorts.Name, null, 500)));
        exception.Code.ShouldBe(AcErrorCodes.WorkspaceNotAllowed);
    }

    [Fact]
    public void Lists_directories_first_and_marks_denied_files()
    {
        var page = _files.List(Query(sort: FileSorts.Name));

        page.Entries.Select(entry => entry.Name).ShouldBe(["docs", "src", "root.txt"]);
        page.Entries[0].IsDir.ShouldBeTrue();
        var docs = _files.List(Query(path: "docs")).Entries;
        docs.Single(entry => entry.Name == "note.txt").Denied.ShouldBeFalse();
        docs.Single(entry => entry.Name == ".env").Denied.ShouldBeTrue();
    }

    [Fact]
    public void Paginates_with_offset_cursor()
    {
        var first = _files.List(Query(limit: 2));
        var second = _files.List(Query(limit: 2, cursor: first.NextCursor));

        first.Entries.Count.ShouldBe(2);
        first.HasMore.ShouldBeTrue();
        first.NextCursor.ShouldBe("2");
        second.Entries.Count.ShouldBe(1);
        second.HasMore.ShouldBeFalse();
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Stats_file_with_sha256()
    {
        var stat = await _files.StatAsync(
            new FileStatQuery(_workspaceId, "docs/note.txt", Hash: true),
            TestContext.Current.CancellationToken);

        stat.Name.ShouldBe("note.txt");
        stat.Size.ShouldBe(5);
        stat.Sha256.ShouldBe("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
        stat.IsDir.ShouldBeFalse();
    }

    [Fact]
    public void Previews_utf8_text()
    {
        var preview = _files.Preview(new FilePreviewQuery(_workspaceId, "docs/note.txt"));

        preview.Kind.ShouldBe("text");
        preview.Text.ShouldBe("hello");
        preview.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Previews_cp949_text()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(
            Path.Combine(_root, "docs", "legacy.txt"),
            Encoding.GetEncoding(949).GetBytes("?쒓?"));

        var preview = _files.Preview(new FilePreviewQuery(_workspaceId, "docs/legacy.txt"));

        preview.Kind.ShouldBe("text");
        preview.Text.ShouldBe("?쒓?");
    }

    [Fact]
    public void Detects_binary_files()
    {
        File.WriteAllBytes(Path.Combine(_root, "docs", "binary.bin"), [0, 1, 2, 3]);

        var preview = _files.Preview(new FilePreviewQuery(_workspaceId, "docs/binary.bin"));

        preview.Kind.ShouldBe("binary");
        preview.Text.ShouldBeNull();
    }

    [Fact]
    public void Previews_png_as_base64()
    {
        File.WriteAllBytes(Path.Combine(_root, "docs", "image.png"), [0x89, 0x50, 0x4E, 0x47, 0, 1]);

        var preview = _files.Preview(new FilePreviewQuery(_workspaceId, "docs/image.png"));

        preview.Kind.ShouldBe("image");
        preview.Base64.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Rejects_preview_and_stat_for_traversal()
    {
        Should.Throw<WorkspacePathException>(() =>
            _files.Preview(new FilePreviewQuery(_workspaceId, "../outside.txt")))
            .Code.ShouldBe(AcErrorCodes.PathInvalid);
    }

    [Fact]
    public void Rejects_preview_for_denied_file()
    {
        var exception = Should.Throw<WorkspaceException>(() =>
            _files.Preview(new FilePreviewQuery(_workspaceId, "docs/.env")));
        exception.Code.ShouldBe(AcErrorCodes.PathDenied);
    }

    [Fact]
    public void Creates_and_renames_directory()
    {
        var created = _files.Mkdir(new FilesMkdirPayload(_workspaceId, "new/folder"));
        created.IsDir.ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "new", "folder")).ShouldBeTrue();

        var renamed = _files.Rename(new FilesRenamePayload(_workspaceId, "new/folder", "renamed"));
        renamed.RelPath.ShouldBe("new/renamed");
        Directory.Exists(Path.Combine(_root, "new", "renamed")).ShouldBeTrue();
    }

    [Fact]
    public void Rename_rejects_conflict_and_invalid_name()
    {
        Should.Throw<WorkspaceException>(() =>
                _files.Rename(new FilesRenamePayload(_workspaceId, "docs/note.txt", ".env")))
            .Code.ShouldBe(AcErrorCodes.FileConflict);

        Should.Throw<WorkspaceException>(() =>
                _files.Rename(new FilesRenamePayload(_workspaceId, "docs/note.txt", "../escape.txt")))
            .Code.ShouldBe(AcErrorCodes.PathInvalid);
    }

    [Fact]
    public void Mkdir_and_rename_respect_workspace_flags()
    {
        var locked = new WorkspaceRegistry(new WorkspaceOptions(
            [new WorkspaceDefinition("locked", _root, true, true, false, false, [])],
            []));
        var files = new FilesSystem(locked);
        var workspaceId = locked.Entries[0].WorkspaceId;

        Should.Throw<WorkspaceException>(() => files.Mkdir(new FilesMkdirPayload(workspaceId, "no")))
            .Code.ShouldBe(AcErrorCodes.WorkspaceNotAllowed);
        Should.Throw<WorkspaceException>(() =>
                files.Rename(new FilesRenamePayload(workspaceId, "docs/note.txt", "renamed.txt")))
            .Code.ShouldBe(AcErrorCodes.WorkspaceNotAllowed);
    }

    [Fact]
    public void Deletes_file_to_recycle_bin_when_allowed()
    {
        var target = Path.Combine(_root, "docs", "note.txt");

        _files.Delete(new FilesDeletePayload(_workspaceId, "docs/note.txt"));

        File.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public void Returns_workspace_info_with_merged_deny_patterns()
    {
        var info = _files.GetWorkspaceInfos([]).Single();

        info.WorkspaceId.ShouldBe(_workspaceId);
        info.Name.ShouldBe("project");
        info.AllowDelete.ShouldBeTrue();
        info.DenyPatterns.ShouldContain("**/.env");
        info.LinkedSessions.ShouldBeEmpty();
    }

    [Fact]
    public void Links_sessions_under_workspace_root()
    {
        var session = CreateSession(Path.Combine(_root, "docs"));
        var outside = CreateSession(Path.Combine(_parent, "elsewhere"));

        var info = _files.GetWorkspaceInfos([session, outside]).Single();

        info.LinkedSessions.ShouldHaveSingleItem().ShouldBe(session.Ref);
    }

    [Fact]
    public void Parses_workspace_options_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["workspaces:0:name"] = "project",
                ["workspaces:0:root"] = _root,
                ["workspaces:0:allowDelete"] = "true",
                ["workspaces:0:denyPatterns:0"] = "**/*.secret",
                ["workspaceParents:0"] = _parent,
            })
            .Build();

        var options = WorkspaceOptions.FromConfiguration(configuration);

        options.Workspaces.ShouldHaveSingleItem().Name.ShouldBe("project");
        options.Workspaces[0].AllowDelete.ShouldBeTrue();
        options.Workspaces[0].DenyPatterns.ShouldContain("**/*.secret");
        options.IsUnderParent(Path.Combine(_root, "docs")).ShouldBeTrue();
        options.IsUnderParent(_parent + "-shadow").ShouldBeFalse();
    }

    private FileListQuery Query(
        string? path = null,
        string sort = FileSorts.Name,
        string? cursor = null,
        int limit = 500) =>
        new(_workspaceId, path, sort, cursor, limit);

    private static SessionSummary CreateSession(string directory) =>
        new(
            new SessionRef(new MachineId("m_test"), new InstanceId("opencode"), Guid.NewGuid().ToString("N")),
            "session",
            null,
            directory,
            AcRunState.Idle,
            ErrorClass.None,
            null,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            SessionDisplayState.Idle,
            null,
            null,
            null,
            true);
}

