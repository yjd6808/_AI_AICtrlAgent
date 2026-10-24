using AiCtrl.Client.Diagnostics;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed record OpenCodeProviderOptions(
    string Username,
    string Password,
    IReadOnlyList<string> Endpoints,
    string? InstanceId = null,
    bool Managed = false);

public sealed class OpenCodeProvider : ICodingAgentProvider
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    private readonly MachineId _machine;
    private readonly OpenCodeProviderOptions _options;
    private readonly ErrorClassifier _classifier;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;

    public OpenCodeProvider(
        MachineId machine,
        OpenCodeProviderOptions options,
        ErrorClassifier classifier,
        TimeProvider time,
        ILoggerFactory logs)
    {
        _machine = machine;
        _options = options;
        _classifier = classifier;
        _time = time;
        _logs = logs;
    }

    public ProviderKind Kind => ProviderKind.OpenCode;

    public async ValueTask<IReadOnlyList<IProviderInstance>> DiscoverAsync(CancellationToken ct)
    {
        var instances = new List<IProviderInstance>();

        foreach (var endpoint in _options.Endpoints)
        {
            ct.ThrowIfCancellationRequested();

            var baseUrl = endpoint.TrimEnd('/');
            OpenCodeProbeResult probe;
            try
            {
                using var probeHttp = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = HttpTimeout };
                var probeApi = new OpenCodeHttpApi(probeHttp, _options.Username, _options.Password);
                var capabilityProbe = new OpenCodeCapabilityProbe(probeApi);
                probe = await capabilityProbe.ProbeAsync(refresh: false, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }

            if (!probe.Reachable)
            {
                continue;
            }

            var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = HttpTimeout };
            var sseClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };
            var api = new OpenCodeHttpApi(http, _options.Username, _options.Password);
            var sse = new OpenCodeSseReader(
                sseClient,
                probe.Routes.Event,
                _options.Username,
                _options.Password,
                _time,
                _logs.CreateLogger<OpenCodeSseReader>());
            var instanceId = string.IsNullOrEmpty(_options.InstanceId) ? new Uri(baseUrl).Authority : _options.InstanceId;

            instances.Add(new OpenCodeInstance(
                _machine,
                instanceId,
                baseUrl,
                probe,
                api,
                new OpenCodeMapper(_classifier),
                sse,
                http,
                sseClient,
                _time,
                _options.Managed));
        }

        return instances;
    }
}
