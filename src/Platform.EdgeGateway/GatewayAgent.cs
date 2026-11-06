using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

/// <summary>Ciclo do gateway: registro unico, sinal de vida, envio de eventos e execucao serial de comandos.</summary>
public sealed class GatewayAgent(
    ICentralClient central,
    JobExecutor executor,
    GatewayOptions options,
    ILogger<GatewayAgent> logger,
    TimeProvider? clock = null,
    EventSpool? spool = null,
    DeviceDirectory? directory = null)
{
    private const int EventBatchSize = 200;

    // Limite por ciclo: comandos sem fim nao podem calar o sinal de vida nem atrasar o envio de eventos.
    private const int MaxJobsPerCycle = 25;
    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private bool enrolled;
    private DateTimeOffset nextHeartbeat = DateTimeOffset.MinValue;

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() >= nextHeartbeat)
        {
            await SendHeartbeatAsync(cancellationToken);
            nextHeartbeat = time.GetUtcNow().Add(options.HeartbeatInterval);
        }

        if (directory is not null)
        {
            directory.Update(await central.ListDevicesAsync(cancellationToken));
        }

        await DrainEventsAsync(cancellationToken);

        for (var executed = 0; executed < MaxJobsPerCycle && await central.LeaseAsync(cancellationToken) is { } job; executed++)
        {
            var result = await executor.ExecuteAsync(job, cancellationToken);

            // Sem repetir o comando: se o resultado nao chegar, a central decide pelo lease.
            await central.SendResultAsync(job.Id, result, cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Comando {JobId} ({Kind}) concluido: {Outcome}.", job.Id, job.Kind, result.Outcome);
            }
        }
    }

    /// <summary>
    /// So apaga depois da resposta da central. Se a resposta se perder o lote volta, e a central deduplica.
    /// Evento recusado sai da fila: repetir nao mudaria a recusa, e travaria os seguintes.
    /// </summary>
    public async Task DrainEventsAsync(CancellationToken cancellationToken)
    {
        if (spool is null)
        {
            return;
        }

        while (true)
        {
            var batch = await spool.ReadBatchAsync(EventBatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }

            var result = await central.SendEventsAsync(new EdgeEventBatch(Guid.NewGuid(), batch.Select(b => b.Event).ToList()), cancellationToken);
            var accepted = result.Accepted.ToHashSet();
            var rejected = result.Rejected.Select(r => r.Id).ToHashSet();

            foreach (var item in batch)
            {
                if (accepted.Contains(item.Event.Id))
                {
                    EventSpool.Remove(item);
                }
                else if (rejected.Contains(item.Event.Id))
                {
                    spool.Drop(item);
                }
            }

            if (result.Rejected.Count > 0 && logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("A central recusou {Count} eventos: {Codes}.", result.Rejected.Count, string.Join(',', result.Rejected.Select(r => r.Code).Distinct()));
            }

            // Sem progresso: a resposta nao tratou nenhum evento. Tenta no proximo ciclo em vez de girar aqui.
            if (accepted.Count == 0 && rejected.Count == 0)
            {
                return;
            }
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var heartbeat = new HeartbeatRequest(Version, spool?.Depth ?? 0, time.GetUtcNow());
        try
        {
            await central.HeartbeatAsync(heartbeat, cancellationToken);
            enrolled = true;
        }
        catch (CentralCallException ex) when (ex.Status == HttpStatusCode.Unauthorized && !enrolled && options.EnrollmentToken is { Length: > 0 } token)
        {
            await central.EnrollAsync(new EnrollRequest(token, Version), cancellationToken);
            logger.LogInformation("Gateway registrado na central.");
            await central.HeartbeatAsync(heartbeat, cancellationToken);
            enrolled = true;
        }
    }
}
