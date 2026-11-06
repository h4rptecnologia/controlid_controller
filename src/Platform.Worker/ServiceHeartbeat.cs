namespace Platform.Worker;

/// <summary>Mantem o processo no ar. Os consumidores reais entram nos epicos seguintes.</summary>
internal sealed class ServiceHeartbeat(ILogger<ServiceHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Platform.Worker iniciado.");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Platform.Worker encerrando.");
        }
    }
}
