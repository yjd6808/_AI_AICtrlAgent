using System.Diagnostics;
using AiCtrl.Core.Api;
using AiCtrl.Core.Workspace;
using Shouldly;

namespace AiCtrl.Tests.Workspace;

public sealed class WorkspacePathPolicyTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "aictrl-paths", Guid.NewGuid().ToString("N"));

    private readonly string _root;

    private readonly List<string> _junctions = [];

    public WorkspacePathPolicyTests()
    {
        _root = Path.Combine(_parent, "workspace");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "docs", "note.txt"), "hello");
    }

    public void Dispose()
    {
        foreach (var junction in _junctions)
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
        }

        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, recursive: true);
        }
    }

    [Fact]
    public void Resolves_root_and_simple_relative_paths()
    {
        var policy = CreatePolicy();

        policy.Resolve(null).FullPath.ShouldBe(_root);
        policy.Resolve("").FullPath.ShouldBe(_root);
        policy.Resolve("docs/note.txt").FullPath.ShouldBe(Path.Combine(_root, "docs", "note.txt"));
        policy.Resolve("docs/").RelativePath.ShouldBe("docs");
        policy.Resolve("docs\\note.txt").RelativePath.ShouldBe("docs/note.txt");
        policy.Resolve("new/child.txt").RelativePath.ShouldBe("new/child.txt");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("docs/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData("docs/a.txt:stream")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("docs/COM1.log")]
    [InlineData("docs/trailing.")]
    [InlineData("docs/trailing ")]
    [InlineData("docs//note.txt")]
    [InlineData("docs/./note.txt")]
    public void Rejects_unsafe_paths(string path)
    {
        var policy = CreatePolicy();

        var exception = Should.Throw<WorkspacePathException>(() => policy.Resolve(path));
        exception.Code.ShouldBe(AcErrorCodes.PathInvalid);
    }

    [Fact]
    public void Rejects_control_characters()
    {
        var policy = CreatePolicy();

        var exception = Should.Throw<WorkspacePathException>(() => policy.Resolve("docs/a\u0001.txt"));
        exception.Code.ShouldBe(AcErrorCodes.PathInvalid);
    }

    [Fact]
    public void Rejects_workspace_prefix_collision()
    {
        Directory.CreateDirectory(_root + "-shadow");
        var policy = CreatePolicy();

        var exception = Should.Throw<WorkspacePathException>(() => policy.Resolve("../workspace-shadow/a.txt"));
        exception.Code.ShouldBe(AcErrorCodes.PathInvalid);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("nested/.env")]
    [InlineData("nested/.env.local")]
    [InlineData("keys/server.pem")]
    [InlineData("keys/client.pfx")]
    [InlineData("keys/id_rsa")]
    [InlineData(".git/config")]
    [InlineData("nested/secrets.json")]
    public void Marks_default_deny_patterns(string path)
    {
        var policy = CreatePolicy();

        policy.IsDenied(path).ShouldBeTrue();
        policy.Resolve(path).RelativePath.ShouldBe(path.Replace('\\', '/'));
    }

    [Fact]
    public void Allows_paths_outside_default_deny_patterns()
    {
        var policy = CreatePolicy();

        policy.IsDenied("docs/note.txt").ShouldBeFalse();
        policy.IsDenied("src/server.cs").ShouldBeFalse();
    }

    [Fact]
    public void Rejects_custom_hard_deny_path()
    {
        var hardDeny = Path.Combine(_root, "private");
        Directory.CreateDirectory(hardDeny);
        var policy = new WorkspacePathPolicy(_root, hardDenyPaths: [hardDeny]);

        var exception = Should.Throw<WorkspacePathException>(() => policy.Resolve("private/file.txt"));
        exception.Code.ShouldBe(AcErrorCodes.PathDenied);
    }

    [Fact]
    public void Rejects_link_that_escapes_workspace()
    {
        var outside = Path.Combine(_parent, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(_root, "escape");
        if (!TryCreateJunction(link, outside))
        {
            Assert.Skip("Junction creation is not available in this environment.");
        }

        var policy = CreatePolicy();

        var exception = Should.Throw<WorkspacePathException>(() => policy.Resolve("escape/secret.txt"));
        exception.Code.ShouldBe(AcErrorCodes.PathDenied);
    }

    [Fact]
    public void Accepts_link_that_stays_inside_workspace()
    {
        var link = Path.Combine(_root, "notes");
        if (!TryCreateJunction(link, Path.Combine(_root, "docs")))
        {
            Assert.Skip("Junction creation is not available in this environment.");
        }

        var policy = CreatePolicy();
        var resolved = policy.Resolve("notes/note.txt");

        resolved.FullPath.ShouldBe(Path.Combine(_root, "docs", "note.txt"));
    }

    [Theory]
    [InlineData("readme.txt", true)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("a:b.txt", false)]
    [InlineData("folder/name.txt", false)]
    [InlineData("name.", false)]
    [InlineData("name ", false)]
    [InlineData("", false)]
    public void Validates_upload_file_names(string name, bool valid)
    {
        WorkspacePathPolicy.IsValidFileName(name).ShouldBe(valid);
    }

    private WorkspacePathPolicy CreatePolicy() => new(_root);

    private bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit(5000);
        if (process.HasExited && process.ExitCode == 0 && Directory.Exists(link))
        {
            _junctions.Add(link);
            return true;
        }

        return false;
    }
}
