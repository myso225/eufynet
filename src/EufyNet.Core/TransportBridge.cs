using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.Core;

/// <summary>
/// Connects the driver and a transport: driver login/state/events → transport (in order, one at a time, so a slow
/// transport never blocks the driver), transport commands → driver → results back.
/// </summary>
public sealed class TransportBridge(IEufyDriver driver, IBridgeTransport transport, ILogger<TransportBridge>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Subscribes before the first await, so start it before the driver.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var outbox = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
        void OnLogin(LoginState s) => outbox.Writer.TryWrite(ct => transport.PublishLoginAsync(s, ct));
        void OnState(StationState s) => outbox.Writer.TryWrite(ct => transport.PublishStateAsync(s, ct));
        void OnEvent(BridgeEvent e) => outbox.Writer.TryWrite(ct => transport.PublishEventAsync(e, ct));
        driver.LoginChanged += OnLogin;
        driver.StateChanged += OnState;
        driver.EventRaised += OnEvent;
        try
        {
            var publishing = PublishAsync(outbox.Reader, cancellationToken);
            await ExecuteCommandsAsync(outbox.Writer, cancellationToken);
            await publishing; // the transport may have no more commands while events keep coming
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            driver.LoginChanged -= OnLogin;
            driver.StateChanged -= OnState;
            driver.EventRaised -= OnEvent;
        }
    }

    private async Task ExecuteCommandsAsync(ChannelWriter<Func<CancellationToken, Task>> outbox, CancellationToken ct)
    {
        await foreach (var command in transport.ReadCommandsAsync(ct))
        {
            var result = await driver.ExecuteAsync(command, ct);
            outbox.TryWrite(c => transport.PublishResultAsync(result, c));
        }
    }

    private async Task PublishAsync(ChannelReader<Func<CancellationToken, Task>> outbox, CancellationToken ct)
    {
        await foreach (var publish in outbox.ReadAllAsync(ct))
        {
            try
            {
                await publish(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Publishing to the transport failed");
            }
        }
    }
}
