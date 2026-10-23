using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AiCtrl.Client.Vpn;

public sealed class VpnHelperClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(150);

    private readonly string _pipeName;

    private readonly string _tokenPath;

    public VpnHelperClient(string tokenPath)
        : this(VpnHelperHost.DefaultPipeName(), tokenPath)
    {
    }

    public VpnHelperClient(string pipeName, string tokenPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        _pipeName = pipeName;
        _tokenPath = tokenPath;
    }

    public string PipeName => _pipeName;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        var result = await RunAsync(
            new VpnHelperCommand(VpnHelperOperations.Ping, null),
            ct).ConfigureAwait(false);
        return result is { ExitCode: CliExitCode.Ok };
    }

    public async Task<ProcessResult?> TryRunAsync(string arguments, CancellationToken ct)
    {
        if (!VpnHelperArguments.TryParse(arguments, out var command))
        {
            return null;
        }

        return await RunAsync(command, ct).ConfigureAwait(false);
    }

    private async Task<ProcessResult?> RunAsync(
        VpnHelperCommand command,
        CancellationToken ct)
    {
        var token = await ReadTokenAsync(ct).ConfigureAwait(false);
        if (token is null)
        {
            return null;
        }

        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        catch (IOException)
        {
            return null;
        }

        await using (pipe)
        {
            using var connectSource = new CancellationTokenSource(ConnectTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(ct, connectSource.Token);
            try
            {
                await pipe.ConnectAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

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

            var request = new VpnHelperRequest(token, command.Operation, command.Argument);
            try
            {
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(request, VpnHelperJsonContext.Default.VpnHelperRequest).AsMemory(),
                    ct).ConfigureAwait(false);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                var response = JsonSerializer.Deserialize(line, VpnHelperJsonContext.Default.VpnHelperResponse);
                if (response is null || response.ProtocolError is not null)
                {
                    return null;
                }

                return new ProcessResult(response.ExitCode, response.StdOut, response.StdErr);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException ex)
            {
                throw new CliException($"VPN helper connection failed: {ex.Message}", CliExitCode.Error);
            }
        }
    }

    private async Task<string?> ReadTokenAsync(CancellationToken ct)
    {
        try
        {
            var token = await File.ReadAllTextAsync(_tokenPath, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class HelperVpnProcessRunner : IProcessRunner
{
    private readonly IProcessRunner _local;

    private readonly VpnHelperClient? _helper;

    public HelperVpnProcessRunner(IProcessRunner local, VpnHelperClient? helper)
    {
        ArgumentNullException.ThrowIfNull(local);
        _local = local;
        _helper = helper;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        if (_helper is not null)
        {
            var remote = await _helper.TryRunAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (remote is not null)
            {
                return remote;
            }
        }

        return await _local.RunAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
    }
}
