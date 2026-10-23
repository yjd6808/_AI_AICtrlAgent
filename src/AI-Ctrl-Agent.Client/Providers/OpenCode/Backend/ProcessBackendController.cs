using System.Diagnostics;
using System.Text;

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
        var launch = ResolveLaunch(_executable);
        var info = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (launch.ScriptPath is not null)
        {
            var command = Quote(launch.ScriptPath, force: true);
            foreach (var argument in _arguments)
            {
                command += " " + Quote(argument, force: true);
            }

            info.Arguments = launch.CommandPrefix + " "
                + launch.ArgumentsWrapper.Replace("{command}", command);
        }
        else
        {
            foreach (var argument in _arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        foreach (var pair in _environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start '{_executable}'.");
        return ValueTask.FromResult<IBackendProcess>(new ProcessBackendProcess(process));
    }

    private static Launch ResolveLaunch(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Launch(executable);
        }

        var resolved = ResolveWindowsPath(executable);
        var extension = Path.GetExtension(resolved);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new Launch("cmd.exe", resolved, "/d /s /c", "\"{command}\"");
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new Launch("powershell.exe", resolved, "-NoProfile -ExecutionPolicy Bypass -File", "{command}");
        }

        return new Launch(resolved);
    }

    private static string ResolveWindowsPath(string executable)
    {
        if (executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar)
            || Path.HasExtension(executable))
        {
            return executable;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return executable;
        }

        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
        {
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), executable + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return executable;
    }

    private static string Quote(string value, bool force = false)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!force && !value.Any(static character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes);
            backslashes = 0;
            quoted.Append(character);
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    private sealed record Launch(
        string FileName,
        string? ScriptPath = null,
        string CommandPrefix = "",
        string ArgumentsWrapper = "{command}");

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
