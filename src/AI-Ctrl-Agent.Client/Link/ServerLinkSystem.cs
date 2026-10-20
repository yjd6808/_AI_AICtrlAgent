using System.Text.Json;
using AiCtrl.Client.Security;
using AiCtrl.Client.Storage;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Runtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Link;

public sealed class ServerLinkSystem : IAcSystem
{
    private readonly ClientDb _db;

    private readonly DeviceKeyStore _deviceKeys;

    private readonly OutboxStore _outbox;

    private readonly ClientRequestHandler _requests;

    private readonly ServerLinkOptions _options;

    private readonly INetworkChangeSignal _network;

    private readonly TimeProvider _time;

    private readonly ILogger<ServerLinkSystem> _logger;

    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;

    private ServerApiClient? _api;

    private string? _token;

    private DateTimeOffset _tokenExpiresAt;

    private int _attempts;

    private int _connected;

    private int _stopped;

    private string? _lastError;

    public ServerLinkSystem(
        ClientDb db,
        DeviceKeyStore deviceKeys,
        OutboxStore outbox,
        ClientRequestHandler requests,
        ServerLinkOptions options,
        INetworkChangeSignal network,
        TimeProvider time,
        ILogger<ServerLinkSystem> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(deviceKeys);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _deviceKeys = deviceKeys;
        _outbox = outbox;
        _requests = requests;
        _options = options;
        _network = network;
        _time = time;
        _logger = logger;
    }

    public string Name => "server-link";

    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    public int Attempts => Volatile.Read(ref _attempts);

    public int ConnectedCount => Volatile.Read(ref _connected);

    public string? LastError => Volatile.Read(ref _lastError);

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Server link disabled by configuration.");
            return;
        }

        if (!await _db.IsEnrolledAsync(ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Server link idle: client is not enrolled.");
            return;
        }

        var key = await _deviceKeys.LoadAsync(ct).ConfigureAwait(false);
        if (key is null)
        {
            _logger.LogWarning("Server link idle: device key is missing.");
            return;
        }

        var url = await _db.GetValueAsync("server.url", ct).ConfigureAwait(false);
        _api = new ServerApiClient(string.IsNullOrWhiteSpace(url) ? _options.BaseUrl : url);
        _loop = Task.Run(() => RunAsync(key, _cts.Token), CancellationToken.None);
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _api?.Dispose();
        _cts.Dispose();
    }

    private async Task RunAsync(DeviceKey key, CancellationToken ct)
    {
        var backoff = _options.InitialBackoff;
        var bootId = Guid.NewGuid().ToString("N");
        while (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _attempts);
            try
            {
                await ConnectAndRunAsync(key, bootId, ct).ConfigureAwait(false);
                backoff = _options.InitialBackoff;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex.Message);
                _logger.LogWarning(ex, "Server link attempt failed.");
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            var delay = Jitter(backoff);
            backoff = TimeSpan.FromMilliseconds(Math.Min(
                _options.MaxBackoff.TotalMilliseconds,
                Math.Max(_options.InitialBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2)));

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var network = _network.WaitAsync(wait.Token);
            var timer = Task.Delay(delay, _time, wait.Token);
            await Task.WhenAny(network, timer).ConfigureAwait(false);
            await wait.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ConnectAndRunAsync(DeviceKey key, string bootId, CancellationToken ct)
    {
        var machineId = await _db.GetMachineIdAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Machine id is missing.");
        var api = _api ?? throw new InvalidOperationException("Server API client is missing.");
        await EnsureTokenAsync(api, key, machineId, ct).ConfigureAwait(false);

        var builder = new HubConnectionBuilder()
            .WithUrl(api.BaseUrl + "/hubs/agent", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
            })
            .WithAutomaticReconnect(new NoReconnectPolicy());

        await using var connection = builder.Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        connection.On<string>("ForceDisconnect", _ => closed.TrySetResult());
        connection.On<RequestPacket>("Request", packet =>
        {
            _ = HandleRequestAsync(connection, packet, ct);
            return Task.CompletedTask;
        });

        await connection.StartAsync(ct).ConfigureAwait(false);
        var lastAcked = await _outbox.GetLastAckedAsync(ct).ConfigureAwait(false);
        var openCommands = await _requests.GetOpenIdsAsync(ct).ConfigureAwait(false);
        var hello = new HelloRequest(
            machineId,
            typeof(ServerLinkSystem).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            bootId,
            lastAcked,
            openCommands.ToArray());
        var ack = await connection.InvokeAsync<HelloAck>("Hello", hello, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Server link connected to {Url}; snapshotRequired={SnapshotRequired}; observedCountry={Country}.",
            api.BaseUrl,
            ack.SnapshotRequired,
            ack.ObservedCountry ?? "unknown");
        Interlocked.Increment(ref _connected);

        await FlushOutboxAsync(connection, machineId, ct).ConfigureAwait(false);
        await _requests
            .ReportUnreportedAsync(response => connection.InvokeAsync("Respond", response, ct), ct)
            .ConfigureAwait(false);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var network = _network.WaitAsync(wait.Token);
        await Task.WhenAny(closed.Task, network).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        await connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EnsureTokenAsync(ServerApiClient api, DeviceKey key, MachineId machineId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_token) && _tokenExpiresAt > _time.GetUtcNow().AddSeconds(30))
        {
            return;
        }

        var challenge = await api.ChallengeAsync(machineId, ct).ConfigureAwait(false);
        var signature = key.Sign(machineId.Value, challenge.Nonce);
        var token = await api.TokenAsync(new AgentTokenRequest(machineId, challenge.Nonce, signature), ct).ConfigureAwait(false);
        _token = token.AccessToken;
        _tokenExpiresAt = _time.GetUtcNow().AddSeconds(token.ExpiresIn);
    }

    private async Task FlushOutboxAsync(HubConnection connection, MachineId machineId, CancellationToken ct)
    {
        while (true)
        {
            var records = await _outbox.GetUnackedAsync(200, ct).ConfigureAwait(false);
            if (records.Count == 0)
            {
                return;
            }

            var events = new List<AgentEventEnvelope>(records.Count);
            foreach (var record in records)
            {
                using var document = JsonDocument.Parse(record.PayloadJson);
                events.Add(new AgentEventEnvelope(record.Seq, record.Type, record.OccurredAt, document.RootElement.Clone()));
            }

            var ack = await connection.InvokeAsync<EventsAck>("PushEvents", new EventBatch(machineId, events), ct).ConfigureAwait(false);
            await _outbox.AckAsync(ack.AckedUpTo, ct).ConfigureAwait(false);
            _logger.LogInformation("Outbox flushed up to sequence {Sequence}.", ack.AckedUpTo);
            if (ack.AckedUpTo < records[^1].Seq)
            {
                return;
            }
        }
    }

    private async Task HandleRequestAsync(HubConnection connection, RequestPacket packet, CancellationToken ct)
    {
        try
        {
            var response = await _requests
                .HandleAsync(
                    packet,
                    acknowledge => connection.InvokeAsync("Ack", acknowledge, ct),
                    ct)
                .ConfigureAwait(false);
            await connection.InvokeAsync("Respond", response, ct).ConfigureAwait(false);
            await _requests.MarkReportedAsync(packet.RequestId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request {RequestId} failed before a response was sent.", packet.RequestId);
        }
    }

    private static TimeSpan Jitter(TimeSpan value)
    {
        var factor = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(value.TotalMilliseconds * factor);
    }

    private sealed class NoReconnectPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => null;
    }
}
