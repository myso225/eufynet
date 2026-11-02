using System.Net;

namespace EufyNet.Cloud;

/// <summary>
/// Throttles requests to at most 5 per second and retries transient failures,
/// matching the got/p-throttle settings in eufy-security-client http/api.js.
/// </summary>
public sealed class EufyHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxRetries = 3;

    private static readonly HashSet<HttpStatusCode> RetryStatusCodes =
    [
        HttpStatusCode.RequestTimeout, HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.Locked,
        HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
        (HttpStatusCode)521, (HttpStatusCode)522, (HttpStatusCode)524,
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            if (attempt >= MaxRetries || !RetryStatusCodes.Contains(response.StatusCode))
                return response;

            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(3 * Math.Pow(2, attempt)), cancellationToken);
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastRequest + MinInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
