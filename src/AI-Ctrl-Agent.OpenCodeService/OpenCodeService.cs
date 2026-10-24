using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace AiCtrl
{
    /// <summary>
    /// Windows service host for the AI-Ctrl OpenCode server.
    /// Keeps opencode.exe serve running with restart backoff and writes logs
    /// in the same format as the previous scheduled-task supervisor.
    /// </summary>
    public sealed class OpenCodeService : ServiceBase
    {
        private const string DefaultServiceName = "AI-Ctrl-OpenCodeServer";
        private const string ConfigFileName = "AiCtrlOpenCodeService.ini";

        private readonly object syncRoot = new object();
        private readonly object logRoot = new object();
        private readonly Dictionary<string, string> config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string baseDirectory;
        private string configPath;
        private static string startupConfigPath;
        private Thread worker;
        private volatile bool stopping;
        private Process child;

        public OpenCodeService()
        {
            ServiceName = DefaultServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            configPath = string.IsNullOrEmpty(startupConfigPath)
                ? Path.Combine(baseDirectory, ConfigFileName)
                : startupConfigPath;

            try
            {
                if (config.Count == 0)
                {
                    LoadConfig(configPath);
                }
            }
            catch (Exception ex)
            {
                Log("configuration error: " + ex.Message);
                throw;
            }

            Log("service started. endpoint=" + BaseUrl + " executable=" + Cfg("Executable") + " log=" + Cfg("LogDirectory"));

            worker = new Thread(RunSupervisor);
            worker.IsBackground = true;
            worker.Name = "opencode-supervisor";
            worker.Start();
        }

        protected override void OnStop()
        {
            stopping = true;
            Log("service stopping.");
            KillChild();
            JoinWorker();
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        private void JoinWorker()
        {
            Thread thread = worker;
            if (thread != null && !thread.Join(TimeSpan.FromSeconds(20)))
            {
                Log("supervisor did not stop within 20 seconds.");
            }
        }

        private void RunSupervisor()
        {
            int delay = 5;

            while (!stopping)
            {
                string executable = Cfg("Executable");
                if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
                {
                    Log("opencode executable not found: " + executable + "; retrying in 60 seconds.");
                    SleepInterruptible(60);
                    continue;
                }

                string password = ReadPassword();
                if (string.IsNullOrEmpty(password))
                {
                    Log("opencode.serve.password is missing or cannot be decrypted: " + SecretsPath + "; retrying in 60 seconds.");
                    SleepInterruptible(60);
                    continue;
                }

                string logDirectory = Cfg("LogDirectory");
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string stdoutPath = Path.Combine(logDirectory, stamp + "-opencode.out.log");
                string stderrPath = Path.Combine(logDirectory, stamp + "-opencode.err.log");

                Process process = null;
                try
                {
                    Directory.CreateDirectory(logDirectory);

                    ProcessStartInfo startInfo = new ProcessStartInfo(executable);
                    startInfo.Arguments = "serve --hostname " + Cfg("ListenHost") + " --port " + Cfg("Port");
                    startInfo.UseShellExecute = false;
                    startInfo.CreateNoWindow = true;
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                    startInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    startInfo.EnvironmentVariables["OPENCODE_SERVER_USERNAME"] = Cfg("Username");
                    startInfo.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"] = password;

                    process = new Process();
                    process.StartInfo = startInfo;

                    Log("starting: " + executable + " " + startInfo.Arguments);

                    process.Start();

                    lock (syncRoot)
                    {
                        child = process;
                    }

                    Log("process started (PID " + process.Id.ToString() + "). stdout=" + stdoutPath + " stderr=" + stderrPath);

                    StartPump(process.StandardOutput.BaseStream, stdoutPath);
                    StartPump(process.StandardError.BaseStream, stderrPath);
                }
                catch (Exception ex)
                {
                    Log("failed to start opencode: " + ex.Message);
                    DisposeProcess(process);
                    SleepInterruptible(delay);
                    delay = Math.Min(delay * 2, 60);
                    continue;
                }

                if (!WaitForListener(process, TimeSpan.FromSeconds(30)))
                {
                    if (stopping)
                    {
                        KillChild();
                        DisposeProcess(process);
                        break;
                    }

                    Log("opencode did not listen on " + BaseUrl + " within 30 seconds.");
                    KillChild();
                    DisposeProcess(process);
                    SleepInterruptible(delay);
                    delay = Math.Min(delay * 2, 60);
                    continue;
                }

                Log("opencode is ready at " + BaseUrl + " (PID " + process.Id.ToString() + ").");
                delay = 5;

                try
                {
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    Log("waiting for opencode failed: " + ex.Message);
                }

                int exitCode = -1;
                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                }

                lock (syncRoot)
                {
                    if (ReferenceEquals(child, process))
                    {
                        child = null;
                    }
                }

                DisposeProcess(process);

                if (stopping)
                {
                    Log("opencode exited with code " + exitCode.ToString() + ".");
                    break;
                }

                Log("opencode exited with code " + exitCode.ToString() + "; restarting in " + delay.ToString() + " seconds.");
                SleepInterruptible(delay);
                delay = Math.Min(delay * 2, 60);
            }
        }

        private bool WaitForListener(Process process, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            string host = Cfg("ListenHost");
            int port;
            if (!int.TryParse(Cfg("Port"), out port))
            {
                port = 4096;
            }

            while (DateTime.UtcNow < deadline && !stopping)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }

                if (CanConnect(host, port, 500))
                {
                    return true;
                }

                Thread.Sleep(500);
            }

            return false;
        }

        private static bool CanConnect(string host, int port, int timeoutMilliseconds)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
                {
                    return false;
                }

                client.EndConnect(result);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }

        private void StartPump(Stream source, string path)
        {
            Thread thread = new Thread(delegate()
            {
                try
                {
                    using (FileStream target = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        byte[] buffer = new byte[16384];
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            target.Write(buffer, 0, read);
                            target.Flush();
                        }
                    }
                }
                catch
                {
                }
            });

            thread.IsBackground = true;
            thread.Start();
        }

        private void KillChild()
        {
            Process process;
            lock (syncRoot)
            {
                process = child;
            }

            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    Log("stopped opencode (PID " + process.Id.ToString() + ").");
                }
            }
            catch
            {
            }
        }

        private static void DisposeProcess(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }

        private void SleepInterruptible(int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!stopping && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
        }

        private string SecretsPath
        {
            get { return Path.Combine(Path.Combine(Cfg("AgentHome"), "data"), "secrets.bin"); }
        }

        private string ReadPassword()
        {
            string path = SecretsPath;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] parts = line.Split(new char[] { '\t' }, 2);
                    if (parts.Length != 2 || parts[0] != "opencode.serve.password")
                    {
                        continue;
                    }

                    byte[] encrypted = Convert.FromBase64String(parts[1]);
                    byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch (Exception ex)
            {
                Log("cannot decrypt the password store: " + ex.Message);
            }

            return null;
        }

        private string BaseUrl
        {
            get { return "http://" + Cfg("ListenHost") + ":" + Cfg("Port"); }
        }

        private string Cfg(string key)
        {
            string value;
            if (config.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            switch (key.ToLowerInvariant())
            {
                case "executable":
                    return @"C:\Users\jdyun\AppData\Roaming\npm\node_modules\@opencode\cli\bin\opencode.exe";
                case "listenhost":
                    return "127.0.0.1";
                case "port":
                    return "4096";
                case "agenthome":
                    return @"C:\AiCtrl\Agent";
                case "logdirectory":
                    return @"C:\AiCtrl\OpenCode\logs";
                case "username":
                    return "opencode";
                default:
                    return string.Empty;
            }
        }

        private void LoadConfig(string path)
        {
            if (!File.Exists(path))
            {
                Log("configuration file not found, using defaults: " + path);
                return;
            }

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                config[key] = value;
            }
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;

            try
            {
                string directory = Cfg("LogDirectory");
                Directory.CreateDirectory(directory);

                lock (logRoot)
                {
                    File.AppendAllText(
                        Path.Combine(directory, "service.log"),
                        line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }

            try
            {
                if (Environment.UserInteractive)
                {
                    Console.WriteLine(line);
                }
            }
            catch
            {
            }
        }

        public void StartForConsole()
        {
            OnStart(new string[0]);
        }

        public void StopForConsole()
        {
            OnStop();
        }

        public static void Main(string[] args)
        {
            List<string> rest = new List<string>();
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--ini", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    startupConfigPath = args[index + 1];
                    index++;
                    continue;
                }

                rest.Add(args[index]);
            }

            if (rest.Count > 0 && string.Equals(rest[0], "--console", StringComparison.OrdinalIgnoreCase))
            {
                OpenCodeService service = new OpenCodeService();

                Console.WriteLine("running in console mode. press CTRL+C or ENTER to stop.");
                service.StartForConsole();

                ManualResetEvent shutdown = new ManualResetEvent(false);
                ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    shutdown.Set();
                };

                Console.CancelKeyPress += handler;

                if (!Console.IsInputRedirected)
                {
                    try
                    {
                        Console.ReadLine();
                    }
                    catch
                    {
                    }

                    shutdown.Set();
                }

                shutdown.WaitOne();
                service.StopForConsole();
                service.JoinWorker();
                return;
            }

            ServiceBase.Run(new OpenCodeService());
        }
    }
}
