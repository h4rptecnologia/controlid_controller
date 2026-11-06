using Platform.Modules.Importation;

namespace Platform.Worker;

/// <summary>Atende a fila da quarentena. Uma falha nao derruba o processo, e a fila vazia espera sem girar.</summary>
internal sealed class ServiceHeartbeat(IServiceProvider services, ILogger<ServiceHeartbeat> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker iniciado.");
        var inspection = services.GetRequiredService<ImportInspectionService>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await inspection.ProcessNextAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (Npgsql.NpgsqlException)
            {
                logger.LogWarning("Banco indisponivel; nova tentativa em instantes.");
            }

            try
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Worker encerrando.");
            }
        }
    }
}

internal sealed class IdleWithoutScanner(ILogger<IdleWithoutScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("Scanner nao configurado: nenhum arquivo sera inspecionado, e todos ficam em quarentena.");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Worker encerrando.");
        }
    }
}
