using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed record SseFrame(string? Data, bool Reconnected);

public sealed class OpenCodeSseReader
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly string _path;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public OpenCodeSseReader(HttpClient client, string path, string username, string password, TimeProvider time, ILogger logger)
    {
        _client = client;
        _path = path;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        _authorization = new AuthenticationHeaderValue("Basic", raw);
        _time = time;
        _logger = logger;
    }

    public async IAsyncEnumerable<SseFrame> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            await using var enumerator = ConnectOnceAsync(ct).GetAsyncEnumerator(ct);
            var failed = false;
            var cancelled = false;

            while (true)
            {
                var hasNext = false;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SSE connection to {Path} dropped; will reconnect.", _path);
                    failed = true;
                }

                if (cancelled)
                {
                    yield break;
                }

                if (failed || !hasNext)
                {
                    break;
                }

                backoff = InitialBackoff;
                yield return enumerator.Current;
            }

            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            yield return new SseFrame(null, true);

            try
            {
                await Task.Delay(backoff, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async IAsyncEnumerable<SseFrame> ConnectOnceAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _path);
        request.Headers.Authorization = _authorization;
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseFrame(data.ToString(), false);
                    data.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line.AsSpan(5).TrimStart(' ');
            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(payload);
        }
    }
}
