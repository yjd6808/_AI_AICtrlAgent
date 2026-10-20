using System.Globalization;
using System.Security.Cryptography;
using AiCtrl.Client.Diagnostics;
using AiCtrl.Client.Providers.OpenCode;
using AiCtrl.Client.Providers.OpenCode.Backend;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

public sealed record ProviderHostOptions
{
    public string Executable { get; init; } = "opencode";

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 4096;

    public string? InstanceId { get; init; }

    public static ProviderHostOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("opencode");
        var port = int.TryParse(section["port"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value is > 0 and <= 65535
                ? value
                : 4096;
        var host = section["host"];

        return new ProviderHostOptions
        {
            Executable = string.IsNullOrWhiteSpace(section["executable"]) ? "opencode" : section["executable"]!,
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host!,
            Port = port,
            InstanceId = string.IsNullOrWhiteSpace(section["instanceId"]) ? null : section["instanceId"],
        };
    }
}

public sealed class ProviderHostSystem : IAcSystem
{
    private const string PasswordKey = "opencode.serve.password";

    private const string HealthPath = "/global/health";

    private const string Username = "opencode";

    private readonly ProviderInstanceRegistry _registry;

    private readonly ProviderHostOptions _options;

    private readonly TimeProvider _time;

    private readonly ILoggerFactory _logs;

    private readonly ILogger<ProviderHostSystem> _logger;

    private readonly ISecretStore _secrets;

    private readonly List<IProviderInstance> _owned = new();

    private HttpClient? _http;

    private OpenCodeBackendSupervisor? _supervisor;

    private AcSystemContext? _ctx;

    public ProviderHostSystem(
        ProviderInstanceRegistry registry,
        ProviderHostOptions options,
        ISecretStore secrets,
        TimeProvider time,
        ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logs);
        _registry = registry;
        _options = options;
        _secrets = secrets;
        _time = time;
        _logs = logs;
        _logger = logs.CreateLogger<ProviderHostSystem>();
    }

    public string Name => "provider-host";

    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("InitAsync must be called before StartAsync.");
        var endpoint = $"http://{_options.Host}:{_options.Port}";
        var password = await _secrets.LoadAsync(PasswordKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(password))
        {
            password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await _secrets.SaveAsync(PasswordKey, password, ct).ConfigureAwait(false);
        }

        _http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(5) };
        var probe = new HttpBackendHealthProbe(_http, HealthPath, Username, password);

        var supervisorOptions = new OpenCodeBackendSupervisorOptions { Endpoint = endpoint, PasswordKey = PasswordKey };
        _supervisor = new OpenCodeBackendSupervisor(
            supervisorOptions,
            controllerPassword => new ProcessBackendController(
                _options.Executable,
                new[] { "serve", "--hostname", _options.Host, "--port", _options.Port.ToString(CultureInfo.InvariantCulture) },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OPENCODE_SERVER_USERNAME"] = Username,
                    ["OPENCODE_SERVER_PASSWORD"] = controllerPassword,
                }),
            probe,
            _secrets,
            _time,
            _logs.CreateLogger<OpenCodeBackendSupervisor>());

        var state = await _supervisor.StartAsync(ct).ConfigureAwait(false);
        if (state.Status == BackendStatus.Healthy)
        {
            _logger.LogInformation("OpenCode backend healthy at {Endpoint} (adopted: {Adopted}, version: {Version}).", endpoint, state.Adopted, state.Version);
        }
        else
        {
            _logger.LogWarning("OpenCode backend unavailable at {Endpoint}: {Status} ({Reason}).", endpoint, state.Status, state.FailReason);
        }

        ctx.Loops.Run("provider-host:backend-watch", _supervisor.WatchAsync);

        var provider = new OpenCodeProvider(
            _registry.MachineId,
            new OpenCodeProviderOptions(Username, password, new[] { endpoint }, _options.InstanceId),
            new ErrorClassifier(),
            _time,
            _logs);

        IReadOnlyList<IProviderInstance> instances;
        try
        {
            instances = await provider.DiscoverAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OpenCode instance discovery failed.");
            instances = Array.Empty<IProviderInstance>();
        }

        _owned.AddRange(instances);
        _registry.Replace(instances);
        _logger.LogInformation("Discovered {Count} OpenCode instance(s).", instances.Count);
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        foreach (var instance in _owned)
        {
            await instance.DisposeAsync().ConfigureAwait(false);
        }

        _owned.Clear();
        _registry.Replace(Array.Empty<IProviderInstance>());
        _http?.Dispose();
        _http = null;
    }
}
