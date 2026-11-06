using System.Globalization;
using System.Text.Json;
using Platform.Modules.DeviceIntegration.ControlId;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

/// <summary>
/// Le do equipamento o que o Monitor pode ter perdido. Cada pagina vai para a central antes de o cursor avancar: uma
/// queda repete uma pagina, e a central deduplica; nunca pula uma. Termina em pagina vazia, nao em pagina curta,
/// porque o limite real de cada firmware e desconhecido.
/// </summary>
public sealed class BackfillRunner(ICentralClient central, TimeProvider? clock = null)
{
    private const int PageSize = 200;
    private const int MaxPagesPerObject = 50;

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public async Task<EdgeJobResult> RunAsync(EdgeJob job, IDeviceConnector device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(device);

        if (job.Backfill is not { } plan || job.Device is null)
        {
            return new EdgeJobResult(job.FencingToken, EdgeJobOutcomes.Failed, "invalid_target", null);
        }

        long? skew = null;
        try
        {
            var system = await device.GetSystemInfoAsync(cancellationToken);
            skew = system.DeviceTimeUnix is { } deviceTime ? deviceTime - time.GetUtcNow().ToUnixTimeSeconds() : null;

            var epochReset = false;
            if (await DetectResetAsync(plan, device, cancellationToken) is { } regressedObject)
            {
                plan = (await central.ResetEpochAsync(job.Id, new EdgeEpochResetRequest(job.FencingToken, plan.SourceEpoch, regressedObject), cancellationToken)).Plan;
                epochReset = true;
            }

            var reports = new List<BackfillObjectReport>();
            var hasMore = false;
            foreach (var cursor in plan.Cursors)
            {
                var (report, more) = await ReadObjectAsync(job, cursor, device, cancellationToken);
                reports.Add(report);
                hasMore |= more;
            }

            // Falha de objeto nao invalida os demais: nem todo firmware tem todos os objetos.
            var outcome = reports.Count > 0 && reports.All(r => r.Failure is not null) ? EdgeJobOutcomes.Failed : EdgeJobOutcomes.Succeeded;
            return new EdgeJobResult(
                job.FencingToken,
                outcome,
                outcome == EdgeJobOutcomes.Failed ? "all_objects_failed" : null,
                null,
                new BackfillReport(plan.SourceEpoch, epochReset, hasMore, skew, reports));
        }
        catch (DeviceCallException ex)
        {
            return new EdgeJobResult(job.FencingToken, EdgeJobOutcomes.Failed, ex.Failure.ToString(), null, new BackfillReport(plan.SourceEpoch, false, false, skew, []));
        }
    }

    /// <summary>
    /// Reset ou troca de equipamento: a linha do cursor mudou de hora, ou nao ha mais nada a partir dela. Linha sumida
    /// com outras depois dela e rotacao normal e nao conta. Se o filtro for ignorado, o parser para antes de chegar aqui.
    /// </summary>
    private static async Task<string?> DetectResetAsync(EdgeBackfillPlan plan, IDeviceConnector device, CancellationToken cancellationToken)
    {
        foreach (var cursor in plan.Cursors)
        {
            if (cursor.LastId is not { } last)
            {
                continue;
            }

            var before = decimal.Parse(last, CultureInfo.InvariantCulture) - 1;
            DeviceObjectPage page;
            try
            {
                page = await device.LoadObjectsPageAsync(
                    new LoadObjectsRequest(cursor.ObjectName, new DeviceObjectCursor(before.ToString(CultureInfo.InvariantCulture), 0), PageSize: 1),
                    cancellationToken);
            }
            catch (DeviceCallException ex) when (ex.Failure == DeviceFailure.DeviceError)
            {
                // Objeto indisponivel neste firmware nao e sinal de reset.
                continue;
            }

            if (page.Rows.Count == 0)
            {
                return cursor.ObjectName;
            }

            var row = page.Rows[0];
            if (row.SourceKey == last && cursor.LastTimeUnix is { } expected && TimeOf(cursor.ObjectName, row) is { } actual && actual != expected)
            {
                return cursor.ObjectName;
            }
        }

        return null;
    }

    private async Task<(BackfillObjectReport Report, bool HasMore)> ReadObjectAsync(EdgeJob job, EdgeBackfillCursor cursor, IDeviceConnector device, CancellationToken cancellationToken)
    {
        var position = cursor.LastId;
        var read = 0;
        var rejected = 0;

        for (var pageNumber = 0; pageNumber < MaxPagesPerObject; pageNumber++)
        {
            DeviceObjectPage page;
            try
            {
                page = await device.LoadObjectsPageAsync(
                    new LoadObjectsRequest(cursor.ObjectName, position is null ? null : new DeviceObjectCursor(position, 0), PageSize),
                    cancellationToken);
            }
            catch (DeviceCallException ex)
            {
                return (new BackfillObjectReport(cursor.ObjectName, read, rejected, position, ex.Failure.ToString()), false);
            }

            if (page.Rows.Count == 0)
            {
                return (new BackfillObjectReport(cursor.ObjectName, read, rejected, position, null), false);
            }

            var observedAt = time.GetUtcNow();
            var events = page.Rows.Select(row => ToEvent(job.Device!.DeviceId, cursor.ObjectName, row, observedAt)).ToList();
            var result = await central.SendEventsAsync(new EdgeEventBatch(Guid.NewGuid(), events), cancellationToken);

            // Recusa aqui e problema de identidade, nao do evento: nao avanca o cursor para nao perder os dados.
            if (result.Rejected.Count > 0)
            {
                return (new BackfillObjectReport(cursor.ObjectName, read, rejected + result.Rejected.Count, position, "rejected_by_central"), false);
            }

            read += page.Rows.Count;
            position = page.Rows[^1].SourceKey;

            // Tambem renova o lease e confirma o fencing: lease perdido encerra aqui, sem dois leitores no mesmo cursor.
            await central.SaveCheckpointAsync(job.Id, new EdgeCheckpointRequest(job.FencingToken, cursor.ObjectName, position, TimeOf(cursor.ObjectName, page.Rows[^1])), cancellationToken);
        }

        return (new BackfillObjectReport(cursor.ObjectName, read, rejected, position, null), true);
    }

    private static EdgeEvent ToEvent(Guid deviceId, string family, DeviceObjectRow row, DateTimeOffset observedAt)
    {
        var allowed = EdgeEventFamilies.AllowedFields[family];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in row.Fields)
        {
            if (allowed.Contains(name) && TextOf(value) is { } text)
            {
                fields[name] = text;
            }
        }

        return new EdgeEvent(
            Guid.NewGuid(),
            deviceId,
            family,
            null,
            row.SourceKey,
            null,
            null, // O device_id de uma linha pode ser de outro leitor da rede; a identidade aqui vem da sessao autenticada.
            TimeOf(family, row),
            observedAt,
            fields,
            EdgeEventIngress.Api);
    }

    private static long? TimeOf(string family, DeviceObjectRow row) =>
        EdgeBackfillObjects.TimeField.TryGetValue(family, out var name) && row.Fields.TryGetValue(name, out var value)
        && long.TryParse(TextOf(value), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    private static string? TextOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
