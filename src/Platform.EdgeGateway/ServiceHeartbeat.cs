using System.Security.Cryptography;

namespace Platform.EdgeGateway;

/// <summary>Sobe o receptor de eventos do terminal junto com o gateway.</summary>
internal sealed class MonitorListenerService(GatewayOptions options, DeviceDirectory directory, EventSpool spool, MonitorCounters counters) : IHostedService
{
    private Microsoft.AspNetCore.Builder.WebApplication? app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        app = MonitorListener.Build(options.MonitorPort, options.MonitorPathPrefix, directory, spool, counters);
        await app.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app is not null)
        {
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
        }
    }
}

/// <summary>Mantem o ciclo rodando e sobrevive a quedas de rede sem repetir comandos.</summary>
internal sealed class ServiceHeartbeat(GatewayAgent agent, GatewayOptions options, ILogger<ServiceHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Gateway iniciado.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await agent.RunCycleAsync(stoppingToken);
            }
            catch (CentralCallException ex)
            {
                logger.LogWarning("Central indisponivel: {Reason}", ex.Message);
            }
            catch (Exception ex) when (ex is TaskCanceledException or TimeoutException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Tempo esgotado ao falar com a central.");
            }

            try
            {
                // Com jitter: gateways que voltam juntos nao chegam todos no mesmo instante.
                var jitter = TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 1000));
                await Task.Delay(options.PollInterval + jitter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Gateway encerrando.");
            }
        }
    }
}
