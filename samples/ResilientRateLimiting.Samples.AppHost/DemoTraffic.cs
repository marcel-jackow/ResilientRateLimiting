using System.Net;

namespace ResilientRateLimiting.Samples.AppHost;

/// <summary>Sends demo traffic to the gateway: a steady load and one-off bursts, each client with its own partition.</summary>
internal sealed class DemoTraffic
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Lock _lock = new();
    private CancellationTokenSource? _steady;

    public bool SteadyRunning
    {
        get { lock (_lock) { return _steady is not null; } }
    }

    public bool StartSteady(Uri gateway, int requestsPerSecond)
    {
        CancellationTokenSource source;
        lock (_lock)
        {
            if (_steady is not null)
            {
                return false;
            }

            source = _steady = new CancellationTokenSource();
        }

        var token = source.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / requestsPerSecond));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    _ = SendAsync(gateway, "steady", token);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped by StopSteady.
            }
        });
        return true;
    }

    public bool StopSteady()
    {
        lock (_lock)
        {
            if (_steady is null)
            {
                return false;
            }

            _steady.Cancel();
            _steady.Dispose();
            _steady = null;
            return true;
        }
    }

    public async Task<(int Allowed, int Limited, int Failed)> BurstAsync(Uri gateway, int count, CancellationToken cancellationToken)
    {
        var client = $"burst-{Guid.NewGuid():N}"[..14];
        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => SendAsync(gateway, client, cancellationToken))).ConfigureAwait(false);
        return (results.Count(s => s == HttpStatusCode.OK), results.Count(s => s == HttpStatusCode.TooManyRequests), results.Count(s => s is not HttpStatusCode.OK and not HttpStatusCode.TooManyRequests));
    }

    private async Task<HttpStatusCode?> SendAsync(Uri gateway, string clientId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, gateway);
            request.Headers.Add("X-Client-Id", clientId);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}
