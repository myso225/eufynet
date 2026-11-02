using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.P2P;

/// <summary>Keeps one station connected: opens a <see cref="P2PSession"/>, waits for it to end, reconnects with backoff.</summary>
public sealed class P2PStationConnector(StationP2PInfo station, P2PSessionOptions options, ILoggerFactory? loggerFactory = null)
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<P2PStationConnector>();

    public P2PSession? Current { get; private set; }

    /// <summary>Raised after each successful connect, before waiting on the session. Subscribe to its events here.</summary>
    public event Action<P2PSession>? Connected;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = MinBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using (var session = new P2PSession(station, options, _loggerFactory.CreateLogger<P2PSession>()))
            {
                Current = session;
                try
                {
                    await session.ConnectAsync(cancellationToken);
                    backoff = MinBackoff;
                    Connected?.Invoke(session);
                    var reason = await session.Completion.WaitAsync(cancellationToken);
                    _logger.LogWarning("P2P connection to {Station} ended: {Reason}", station.StationSn, reason);
                }
                catch (TimeoutException ex)
                {
                    _logger.LogWarning("P2P connect to {Station} failed: {Message}", station.StationSn, ex.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    Current = null;
                }
            }

            _logger.LogInformation("Reconnecting to {Station} in {Delay:0}s", station.StationSn, backoff.TotalSeconds);
            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }
}
