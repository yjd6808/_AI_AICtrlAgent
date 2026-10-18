using System.Reflection;
using System.Xml.Linq;
using AiCtrl.Core.Runtime;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Architecture;

public sealed class DependencyDirectionTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static readonly string[] CoreAllowedPrefixes =
    [
        "System",
        "Microsoft.Extensions",
        "YamlDotNet",
        "netstandard",
        "mscorlib",
    ];

    [Fact]
    public void Core_assembly_references_only_allowed_assemblies()
    {
        foreach (var name in ReferencedNames(typeof(AcApp).Assembly))
        {
            var allowed = CoreAllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
            allowed.ShouldBeTrue($"Core가 허용되지 않은 어셈블리를 참조합니다: {name}");
        }
    }

    [Fact]
    public void Core_uses_only_allowed_packages()
    {
        foreach (var package in PackageReferencesOf(@"src\AI-Ctrl-Agent.Core\AI-Ctrl-Agent.Core.csproj"))
        {
            var allowed = package == "YamlDotNet" || package.StartsWith("Microsoft.Extensions", StringComparison.Ordinal);
            allowed.ShouldBeTrue($"Core가 허용되지 않은 패키지를 참조합니다: {package}");
        }
    }

    [Fact]
    public void Core_references_no_other_project()
    {
        ProjectReferencesOf(@"src\AI-Ctrl-Agent.Core\AI-Ctrl-Agent.Core.csproj").ShouldBeEmpty();
    }

    [Fact]
    public void Server_references_core_only()
    {
        ProjectReferencesOf(@"src\AI-Ctrl-Agent.Server\AI-Ctrl-Agent.Server.csproj").ShouldBe(["AI-Ctrl-Agent.Core"]);
    }

    [Fact]
    public void Client_references_core_only()
    {
        ProjectReferencesOf(@"src\AI-Ctrl-Agent.Client\AI-Ctrl-Agent.Client.csproj").ShouldBe(["AI-Ctrl-Agent.Core"]);
    }

    [Fact]
    public void ApiClient_references_core_only()
    {
        ProjectReferencesOf(@"src\AI-Ctrl-Agent.ApiClient\AI-Ctrl-Agent.ApiClient.csproj").ShouldBe(["AI-Ctrl-Agent.Core"]);
    }

    [Fact]
    public void ServerMCP_references_core_and_api_client_only()
    {
        ProjectReferencesOf(@"src\AI-Ctrl-Agent.ServerMCP\AI-Ctrl-Agent.ServerMCP.csproj")
            .ShouldBe(["AI-Ctrl-Agent.ApiClient", "AI-Ctrl-Agent.Core"]);
    }

    [Fact]
    public void ClientMCP_references_core_and_api_client_only()
    {
        ProjectReferencesOf(@"src\AI-Ctrl-Agent.ClientMCP\AI-Ctrl-Agent.ClientMCP.csproj")
            .ShouldBe(["AI-Ctrl-Agent.ApiClient", "AI-Ctrl-Agent.Core"]);
    }

    private static string[] ProjectReferencesOf(string relativePath) =>
        ElementIncludes(relativePath, "ProjectReference")
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] PackageReferencesOf(string relativePath) =>
        ElementIncludes(relativePath, "PackageReference")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> ElementIncludes(string relativePath, string elementName)
    {
        var document = XDocument.Load(Path.Combine(Root, relativePath));
        return document.Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrEmpty(include))
            .Select(include => include!);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AI-Ctrl-Agent.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("AI-Ctrl-Agent.sln을 찾을 수 없습니다.");
    }

    private static IEnumerable<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => !string.IsNullOrEmpty(name));
}
