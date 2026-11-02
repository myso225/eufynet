using EufyNet.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EufyNet.App;

/// <summary>
/// Runs one transport bridge per transport (console, MQTT) and the driver; stops the app when the driver ends on its own
/// (list only, login failure).
/// </summary>
internal sealed class BridgeService(EufyDriver driver, IEnumerable<IBridgeTransport> transports, IHostApplicationLifetime lifetime,
    ILoggerFactory loggerFactory, ILogger<BridgeService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe before the driver raises anything.
        var bridging = transports
            .Select(t => new TransportBridge(driver, t, loggerFactory.CreateLogger<TransportBridge>()).RunAsync(stoppingToken))
            .ToList();
        try
        {
            await driver.RunAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
                lifetime.StopApplication(); // --list-only
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ctrl+C / docker stop: the connectors have closed their sessions.
        }
        catch (EufyLoginFailedException ex)
        {
            logger.LogError("{Message}", ex.Message);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The driver stopped");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        await Task.WhenAll(bridging);
        logger.LogInformation("Stopped");
    }
}
