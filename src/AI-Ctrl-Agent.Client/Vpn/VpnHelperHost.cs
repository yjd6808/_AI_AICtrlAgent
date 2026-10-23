using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AiCtrl.Client.Storage;
using AiCtrl.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Vpn;

public sealed class VpnHelperHost
{
    public const string PipeNamePrefix = "aictrl-vpn-";

    private readonly string _pipeName;

    private readonly string _token;

    private readonly VpnOptions _options;

    private readonly IProcessRunner _runner;

    private readonly string? _executablePath;

    private readonly object _knownServersGate = new();

    private readonly HashSet<string> _knownServers = new(StringComparer.OrdinalIgnoreCase);

    public VpnHelperHost(
        string pipeName,
        string token,
        VpnOptions options,
        IProcessRunner runner,
        string? executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        _pipeName = pipeName;
        _token = token;
        _options = options;
        _runner = runner;
        _executablePath = executablePath;
    }

    public static string DefaultPipeName()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                return PipeNamePrefix + sid;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        return PipeNamePrefix + Environment.UserName;
    }

    public static async Task<int> RunConsoleAsync(VpnOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsElevated())
        {
            Console.Error.WriteLine("VPN helper must run from an elevated terminal.");
            AcLog.Write(LogLevel.Error, "VpnHelper", "VPN helper was started without administrator privileges.");
            return CliExitCode.Privilege;
        }

        ClientPaths.EnsureDataDirectory();
        var tokenPath = options.HelperTokenPath;
        var token = CreateToken();
        await File.WriteAllTextAsync(tokenPath, token, ct).ConfigureAwait(false);
        var executablePath = ProtonVpnCli.ResolveExecutable();
        try
        {
            AcLog.Write(
                LogLevel.Information,
                "VpnHelper",
                $"VPN helper listening on {DefaultPipeName()} (CLI: {executablePath ?? "not found"}).");
            Console.WriteLine($"VPN helper listening on {DefaultPipeName()}.");
            Console.WriteLine("Press Ctrl+C to stop.");
            var host = new VpnHelperHost(
                DefaultPipeName(),
                token,
                options,
                new ProcessRunner(),
                executablePath);
            await host.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            try
            {
                File.Delete(tokenPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            AcLog.Write(LogLevel.Information, "VpnHelper", "VPN helper stopped.");
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = CreatePipe(_pipeName);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                AcLog.Write(LogLevel.Warning, "VpnHelper", $"VPN helper connection failed: {ex.Message}");
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
        };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null)
        {
            return;
        }

        VpnHelperRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, VpnHelperJsonContext.Default.VpnHelperRequest);
        }
        catch (JsonException)
        {
            await WriteAsync(writer, new VpnHelperResponse(CliExitCode.Error, string.Empty, string.Empty, "invalid_request"), ct)
                .ConfigureAwait(false);
            return;
        }

        if (request is null || !TokenMatches(request.Token))
        {
            await WriteAsync(writer, new VpnHelperResponse(CliExitCode.Error, string.Empty, string.Empty, "unauthorized"), ct)
                .ConfigureAwait(false);
            return;
        }

        var command = new VpnHelperCommand(request.Operation, request.Argument);
        if (command.Operation != VpnHelperOperations.Ping)
        {
            var rawArguments = BuildArguments(command);
            if (rawArguments is null || !VpnHelperArguments.TryParse(rawArguments, out var validated))
            {
                await WriteAsync(writer, new VpnHelperResponse(CliExitCode.Usage, string.Empty, string.Empty, "invalid_operation"), ct)
                    .ConfigureAwait(false);
                return;
            }

            command = validated;
        }

        var response = await ExecuteAsync(command, ct).ConfigureAwait(false);
        await WriteAsync(writer, response, ct).ConfigureAwait(false);
    }

    private async Task<VpnHelperResponse> ExecuteAsync(VpnHelperCommand command, CancellationToken ct)
    {
        if (command.Operation == VpnHelperOperations.Ping)
        {
            return new VpnHelperResponse(CliExitCode.Ok, string.Empty, string.Empty, null);
        }

        if (command.Operation == VpnHelperOperations.Connect && !IsKnownServer(command.Argument))
        {
            return new VpnHelperResponse(
                CliExitCode.Usage,
                string.Empty,
                "VPN server is not in the recent server list.",
                null);
        }

        var executablePath = _executablePath ?? ProtonVpnCli.ResolveExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new VpnHelperResponse(CliExitCode.Error, string.Empty, "protonvpn CLI was not found.", null);
        }

        var arguments = VpnHelperArguments.ToCliArguments(command);
        var timeout = command.Operation == VpnHelperOperations.Connect
            ? _options.ConnectTimeout
            : _options.QueryTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        try
        {
            var result = await _runner
                .RunAsync(executablePath, arguments, linkedSource.Token)
                .ConfigureAwait(false);
            if (command.Operation == VpnHelperOperations.Servers
                && result.ExitCode == CliExitCode.Ok)
            {
                RememberServers(result.StdOut);
            }

            return new VpnHelperResponse(result.ExitCode, result.StdOut, result.StdErr, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VpnHelperResponse(
                CliExitCode.Error,
                string.Empty,
                $"protonvpn {arguments} timed out.",
                null);
        }
        catch (CliException ex)
        {
            return new VpnHelperResponse(ex.ExitCode, string.Empty, ex.Message, null);
        }
    }

    private bool IsKnownServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        lock (_knownServersGate)
        {
            return _knownServers.Contains(server);
        }
    }

    private void RememberServers(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (!document.RootElement.TryGetProperty("servers", out var servers)
                || servers.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            lock (_knownServersGate)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        _knownServers.Add(name.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private static string? BuildArguments(VpnHelperCommand command)
    {
        try
        {
            return VpnHelperArguments.ToCliArguments(command);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private bool TokenMatches(string? provided)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(_token);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static Task WriteAsync(StreamWriter writer, VpnHelperResponse response, CancellationToken ct) =>
        writer.WriteLineAsync(
            JsonSerializer.Serialize(response, VpnHelperJsonContext.Default.VpnHelperResponse)
                .AsMemory(),
            ct);

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
