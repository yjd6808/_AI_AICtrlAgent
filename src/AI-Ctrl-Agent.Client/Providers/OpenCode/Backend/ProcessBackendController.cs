using System.Diagnostics;

namespace AiCtrl.Client.Providers.OpenCode.Backend;

public sealed class ProcessBackendController : IBackendProcessController
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _arguments;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public ProcessBackendController(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        _executable = executable;
        _arguments = arguments;
        _environment = environment;
    }

    public ValueTask<IBackendProcess> StartAsync(CancellationToken ct)
    {
        var info = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in _arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var pair in _environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start '{_executable}'.");
        return ValueTask.FromResult<IBackendProcess>(new ProcessBackendProcess(process));
    }

    private sealed class ProcessBackendProcess : IBackendProcess
    {
        private readonly Process _process;

        public ProcessBackendProcess(Process process)
        {
            _process = process;
        }

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public async Task KillAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public ValueTask DisposeAsync()
        {
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
