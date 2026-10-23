using AiCtrl.Client.Vpn;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class VpnHelperTests
{
    [Fact]
    public void Parses_exact_cli_arguments()
    {
        VpnHelperArguments.TryParse("status --json", out var status).ShouldBeTrue();
        status.Operation.ShouldBe(VpnHelperOperations.Status);

        VpnHelperArguments.TryParse("connect JP#12 --json --yes", out var connect).ShouldBeTrue();
        connect.Argument.ShouldBe("JP#12");

        VpnHelperArguments.TryParse("servers --country jp --json --limit 2000", out var servers)
            .ShouldBeTrue();
        servers.Argument.ShouldBe("JP");

        VpnHelperArguments.ToCliArguments(servers)
            .ShouldBe("servers --country JP --json --limit 2000");
    }

    [Theory]
    [InlineData("connect JP#1;shutdown --json --yes")]
    [InlineData("connect ../../evil --json --yes")]
    [InlineData("servers --country JP;rm --json --limit 2000")]
    [InlineData("status --json --verbose")]
    [InlineData("")]
    public void Rejects_unsupported_or_injected_arguments(string arguments)
    {
        VpnHelperArguments.TryParse(arguments, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Authorized_request_round_trips_through_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = "aictrl-vpn-test-" + Guid.NewGuid().ToString("N");
        var tokenPath = Path.Combine(Path.GetTempPath(), $"aictrl-vpn-{Guid.NewGuid():N}.token");
        var token = "correct-token";
        await File.WriteAllTextAsync(tokenPath, token, ct);
        var runner = new RecordingRunner("""{"signedIn":true}""");
        var host = new VpnHelperHost(pipeName, token, new VpnOptions(), runner, "protonvpn.exe");
        using var hostCts = new CancellationTokenSource();
        var hostTask = host.RunAsync(hostCts.Token);
        var client = new VpnHelperClient(pipeName, tokenPath);

        try
        {
            await WaitUntilAvailableAsync(client, ct);
            var result = await client.TryRunAsync("status --json", ct);

            result.ShouldNotBeNull();
            result.ExitCode.ShouldBe(CliExitCode.Ok);
            result.StdOut.ShouldBe("""{"signedIn":true}""");
            runner.Calls.ShouldHaveSingleItem().ShouldBe("status --json");
        }
        finally
        {
            hostCts.Cancel();
            await hostTask;
            File.Delete(tokenPath);
        }
    }

    [Fact]
    public async Task Connect_is_rejected_until_servers_were_loaded()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = "aictrl-vpn-test-" + Guid.NewGuid().ToString("N");
        var tokenPath = Path.Combine(Path.GetTempPath(), $"aictrl-vpn-{Guid.NewGuid():N}.token");
        await File.WriteAllTextAsync(tokenPath, "token", ct);
        var runner = new ScriptedRunner(args => args.StartsWith("servers ", StringComparison.Ordinal)
            ? """{"servers":[{"name":"JP#1"}]}"""
            : """{"connected":{"server":"JP#1"}}""");
        var host = new VpnHelperHost(pipeName, "token", new VpnOptions(), runner, "protonvpn.exe");
        using var hostCts = new CancellationTokenSource();
        var hostTask = host.RunAsync(hostCts.Token);
        var client = new VpnHelperClient(pipeName, tokenPath);

        try
        {
            await WaitUntilAvailableAsync(client, ct);
            var rejected = await client.TryRunAsync("connect JP#1 --json --yes", ct);

            rejected.ShouldNotBeNull();
            rejected.ExitCode.ShouldBe(CliExitCode.Usage);
            rejected.StdErr.ShouldContain("recent server list");
            runner.Calls.ShouldBeEmpty();

            var servers = await client.TryRunAsync("servers --country JP --json --limit 2000", ct);
            servers.ShouldNotBeNull();
            servers.ExitCode.ShouldBe(CliExitCode.Ok);

            var connected = await client.TryRunAsync("connect JP#1 --json --yes", ct);
            connected.ShouldNotBeNull();
            connected.ExitCode.ShouldBe(CliExitCode.Ok);
            runner.Calls.ShouldContain("connect JP#1 --json --yes");
        }
        finally
        {
            hostCts.Cancel();
            await hostTask;
            File.Delete(tokenPath);
        }
    }

    [Fact]
    public async Task Wrong_token_is_rejected_before_cli_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = "aictrl-vpn-test-" + Guid.NewGuid().ToString("N");
        var wrongTokenPath = Path.Combine(Path.GetTempPath(), $"aictrl-vpn-{Guid.NewGuid():N}.token");
        var goodTokenPath = Path.Combine(Path.GetTempPath(), $"aictrl-vpn-{Guid.NewGuid():N}.token");
        await File.WriteAllTextAsync(wrongTokenPath, "wrong-token", ct);
        await File.WriteAllTextAsync(goodTokenPath, "correct-token", ct);
        var runner = new RecordingRunner("{}");
        var host = new VpnHelperHost(pipeName, "correct-token", new VpnOptions(), runner, "protonvpn.exe");
        using var hostCts = new CancellationTokenSource();
        var hostTask = host.RunAsync(hostCts.Token);
        var correctClient = new VpnHelperClient(pipeName, goodTokenPath);
        var wrongClient = new VpnHelperClient(pipeName, wrongTokenPath);

        try
        {
            await WaitUntilAvailableAsync(correctClient, ct);
            (await wrongClient.IsAvailableAsync(ct)).ShouldBeFalse();
            (await wrongClient.TryRunAsync("status --json", ct)).ShouldBeNull();
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            hostCts.Cancel();
            await hostTask;
            File.Delete(wrongTokenPath);
            File.Delete(goodTokenPath);
        }
    }

    private static async Task WaitUntilAvailableAsync(VpnHelperClient client, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await client.IsAvailableAsync(ct))
            {
                return;
            }

            await Task.Delay(20, ct);
        }

        throw new TimeoutException("The VPN helper pipe did not become available.");
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Func<string, string> _stdOut;

        public ScriptedRunner(Func<string, string> stdOut)
        {
            _stdOut = stdOut;
        }

        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken = default)
        {
            fileName.ShouldBe("protonvpn.exe");
            Calls.Add(arguments);
            return Task.FromResult(new ProcessResult(CliExitCode.Ok, _stdOut(arguments), string.Empty));
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly string _stdOut;

        public RecordingRunner(string stdOut)
        {
            _stdOut = stdOut;
        }

        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken = default)
        {
            fileName.ShouldBe("protonvpn.exe");
            Calls.Add(arguments);
            return Task.FromResult(new ProcessResult(CliExitCode.Ok, _stdOut, string.Empty));
        }
    }
}
