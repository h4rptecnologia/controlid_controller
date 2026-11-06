using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.Fleet;

/// <summary>Recebe lotes do gateway. Reenviar o mesmo lote nao duplica nada e continua respondendo aceito.</summary>
internal static class EdgeEventEndpoints
{
    private const int MaxEventsPerBatch = 500;
    private const int MaxFieldLength = 256;

    public static async Task<IResult> ListDevicesAsync(HttpContext http, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var gatewayId = http.GatewayOf();
        var devices = await database.InTenantAsync(
            http.TenantOf(),
            tx => tx.QueryAsync<DeviceRow>(
                "SELECT id AS DeviceId, host(endpoint_host) AS Host FROM hub.devices WHERE gateway_id = @gatewayId AND endpoint_host IS NOT NULL",
                new { gatewayId }),
            cancellationToken);

        return Results.Ok(new EdgeDeviceList(devices.Select(d => new EdgeDevice(d.DeviceId, d.Host)).ToList()));
    }

    public static async Task<IResult> ReceiveBatchAsync(EdgeEventBatch body, HttpContext http, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.Events is not { Count: <= MaxEventsPerBatch })
        {
            return Problems.Invalid($"O lote aceita até {MaxEventsPerBatch} eventos.");
        }

        var gatewayId = http.GatewayOf();
        var correlation = http.GetCorrelationId();

        var result = await database.InTenantAsync(
            http.TenantOf(),
            async tx =>
            {
                var devices = (await tx.QueryAsync<GatewayDeviceRow>(
                        "SELECT id AS Id, source_device_id::text AS SourceDeviceId, source_epoch AS SourceEpoch FROM hub.devices WHERE gateway_id = @gatewayId",
                        new { gatewayId }))
                    .ToDictionary(d => d.Id);

                var accepted = new List<Guid>();
                var rejected = new List<RejectedEdgeEvent>();
                foreach (var item in body.Events)
                {
                    var code = Rejection(item, devices);
                    if (code is not null)
                    {
                        rejected.Add(new RejectedEdgeEvent(item.Id, code));
                        continue;
                    }

                    await PersistAsync(tx, item, gatewayId, devices[item.DeviceId].SourceEpoch);
                    accepted.Add(item.Id);
                }

                if (rejected.Count > 0)
                {
                    // Sem conteudo: so os motivos e a quantidade, para investigar tentativas de falsificacao.
                    await AuditWriter.WriteAsync(tx, new AuditEntry(
                        null, "edge.events_rejected", "gateway", gatewayId.ToString(), AuditWriter.Denied, correlation,
                        Metadata: new { count = rejected.Count, codes = rejected.Select(r => r.Code).Distinct().ToArray() }));
                }

                return new EdgeEventBatchResult(accepted, rejected);
            },
            cancellationToken);

        return Results.Ok(result);
    }

    private static string? Rejection(EdgeEvent item, Dictionary<Guid, GatewayDeviceRow> devices)
    {
        if (!devices.TryGetValue(item.DeviceId, out var device))
        {
            return "device_not_of_gateway";
        }

        if (item.Ingress is not (null or EdgeEventIngress.Monitor or EdgeEventIngress.Api))
        {
            return "unknown_ingress";
        }

        if (!EdgeEventFamilies.AllowedFields.TryGetValue(item.Family, out var allowed))
        {
            return "unknown_family";
        }

        // A central nao confia no gateway para sanear: campo fora da lista recusa o evento inteiro.
        if (item.Fields is null || item.Fields.Keys.Any(name => !allowed.Contains(name)) || item.Fields.Values.Any(v => v is null || v.Length > MaxFieldLength))
        {
            return "field_not_allowed";
        }

        // Sem identidade aprendida no diagnostico nao ha com o que comparar o device_id declarado.
        if (device.SourceDeviceId is null)
        {
            return "device_not_verified";
        }

        return item.SourceDeviceId is { } declared && declared != device.SourceDeviceId ? "device_id_mismatch" : null;
    }

    private static async Task PersistAsync(TenantTransaction tx, EdgeEvent item, Guid gatewayId, int epoch)
    {
        if (item.Family == EdgeEventFamilies.DeviceAlive)
        {
            // Sinal de vida atualiza o estado e nao vira historico: seria um registro a cada 30 segundos por aparelho.
            await tx.ExecuteAsync(
                """
                UPDATE hub.devices
                SET last_seen_at = GREATEST(last_seen_at, @observed), status = 'reachable', reported_access_logs = COALESCE(@logs, reported_access_logs),
                    clock_skew_seconds = COALESCE(@skew, clock_skew_seconds)
                WHERE id = @DeviceId
                """,
                new
                {
                    observed = item.ObservedAt.UtcDateTime,
                    logs = item.Fields.TryGetValue("access_logs", out var text) && long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : (long?)null,

                    // Hora do aparelho comparada com a hora em que o gateway recebeu (RF-050).
                    skew = item.OccurredAtUnix is { } alive ? (int?)Math.Clamp(alive - item.ObservedAt.ToUnixTimeSeconds(), int.MinValue, int.MaxValue) : null,
                    item.DeviceId,
                });
            return;
        }

        var occurred = item.OccurredAtUnix is { } seconds && seconds is > 0 and < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : (DateTime?)null;

        await tx.ExecuteAsync(
            """
            INSERT INTO hub.access_events
              (tenant_id, device_id, source_family, source_epoch, source_event_id, source_uuid, source_event_code, event_kind,
               occurred_at, raw_time, ingress_source, dedup_hash, details, observed_at, gateway_id)
            VALUES
              (@TenantId, @DeviceId, @Family, @epoch, @SourceEventId, @SourceUuid, @code, @kind,
               @occurred, @rawTime, @ingress, @hash, @details::jsonb, @observed, @gatewayId)
            ON CONFLICT (tenant_id, device_id, source_family, source_epoch, dedup_hash) DO NOTHING
            """,
            new
            {
                tx.TenantId,
                item.DeviceId,
                item.Family,
                epoch,
                ingress = item.Ingress ?? EdgeEventIngress.Monitor,
                SourceEventId = Limit(item.SourceEventId),
                SourceUuid = Limit(item.SourceUuid),
                code = item.Fields.GetValueOrDefault("event") ?? item.Fields.GetValueOrDefault("type"),
                kind = EventClassifier.Classify(item),
                occurred,
                rawTime = item.OccurredAtUnix?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                hash = DedupHash(item),
                details = JsonSerializer.Serialize(item.Fields),
                observed = item.ObservedAt.UtcDateTime,
                gatewayId,
            });
    }

    /// <summary>
    /// access_logs e alarm_logs usam so o id da origem, para o evento visto pelo Monitor coincidir com o mesmo evento
    /// lido depois por load_objects. Quem nao tem id usa o conteudo; secbox nao tem hora, entao usa o id do gateway.
    /// </summary>
    internal static byte[] DedupHash(EdgeEvent item)
    {
        var key = item.Family switch
        {
            EdgeEventFamilies.AccessLogs or EdgeEventFamilies.AlarmLogs or EdgeEventFamilies.AccessEvents or EdgeEventFamilies.Templates or EdgeEventFamilies.Cards
                when item.SourceEventId is { } id => item.ChangeType is null or "inserted" ? $"{item.Family}|{id}" : $"{item.Family}|{id}|{item.ChangeType}|{item.ObservedAt.ToUnixTimeMilliseconds()}",
            EdgeEventFamilies.SecBox => $"{item.Family}|{item.Id:N}",
            _ when item.OccurredAtUnix is null => $"{item.Family}|{item.Id:N}",
            _ => string.Join('|', new[] { item.Family, item.SourceUuid ?? string.Empty, item.OccurredAtUnix?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty }
                .Concat(item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"))),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    private static string? Limit(string? value) => value is { Length: > 128 } ? value[..128] : value;

    private sealed record DeviceRow(Guid DeviceId, string Host);

    private sealed record GatewayDeviceRow(Guid Id, string? SourceDeviceId, int SourceEpoch);
}

/// <summary>
/// Classificacao de negocio; o codigo original fica guardado. Codigo desconhecido nao vira "negado" nem "concedido".
/// Autorizacao e passagem sao coisas distintas: giro e porta nao confirmam acesso, e acesso nao confirma passagem.
/// </summary>
internal static class EventClassifier
{
    // Ordem da lista oficial do campo event de access_logs.
    private static readonly Dictionary<string, string> AccessEvents = new(StringComparer.Ordinal)
    {
        ["1"] = "invalid_device",
        ["2"] = "invalid_identification_parameters",
        ["3"] = "not_identified",
        ["4"] = "identification_pending",
        ["5"] = "identification_timeout",
        ["6"] = "access_denied",
        ["7"] = "access_granted",
        ["8"] = "access_pending",
        ["9"] = "not_administrator",
        ["10"] = "unidentified_access",
        ["11"] = "request_to_exit",
        ["12"] = "web_interface_access",
        ["13"] = "gave_up",
        ["14"] = "no_response",
        ["15"] = "intercom_access",
    };

    public static string Classify(EdgeEvent item)
    {
        switch (item.Family)
        {
            case EdgeEventFamilies.AccessLogs:
                if (item.ChangeType is not (null or "inserted"))
                {
                    return "log_" + (item.ChangeType == "deleted" ? "deleted" : "updated");
                }

                return item.Fields.TryGetValue("event", out var code) && AccessEvents.TryGetValue(code, out var kind) ? kind : "unknown";
            case EdgeEventFamilies.AlarmLogs:
                return item.Fields.GetValueOrDefault("event") switch
                {
                    "1" => "alarm_activated",
                    "2" => "alarm_deactivated",
                    _ => "unknown",
                };
            case EdgeEventFamilies.AccessEvents:
                return item.Fields.GetValueOrDefault("type") switch
                {
                    "OPEN" => "door_opened",
                    "CLOSE" => "door_closed",
                    "TURN_LEFT" => "turnstile_turn_left",
                    "TURN_RIGHT" => "turnstile_turn_right",
                    "GIVE_UP" => "turnstile_gave_up",
                    _ => "unknown",
                };
            case EdgeEventFamilies.Templates or EdgeEventFamilies.Cards:
                return $"credential_{item.ChangeType ?? "changed"}";
            case EdgeEventFamilies.Door or EdgeEventFamilies.SecBox:
                return item.Fields.GetValueOrDefault("open") switch
                {
                    "true" => "door_opened",
                    "false" => "door_closed",
                    _ => "unknown",
                };
            case EdgeEventFamilies.CatraEvent:
                return (item.Fields.GetValueOrDefault("name") ?? string.Empty).ToUpperInvariant().Replace('_', ' ') switch
                {
                    "TURN LEFT" or "EVENT TURN LEFT" => "turnstile_turn_left",
                    "TURN RIGHT" or "EVENT TURN RIGHT" => "turnstile_turn_right",
                    "GIVE UP" or "EVENT GIVE UP" => "turnstile_gave_up",
                    _ => "unknown",
                };
            case EdgeEventFamilies.OperationMode:
                return "operation_mode_changed";
            case EdgeEventFamilies.UsbDrive:
                return "usb_drive";
            default:
                return "unknown";
        }
    }
}
