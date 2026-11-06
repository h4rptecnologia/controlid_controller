using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.Fleet;

public sealed record DeviceCursorState(string ObjectName, string LastId, DateTime UpdatedAt);

/// <param name="ClockStatus">unknown, ok ou skewed. Nunca "ok" sem medicao.</param>
/// <param name="PossiblyMissing">
/// O equipamento diz ter mais logs do que os guardados aqui. E indicio, nao prova: os dois contadores nao sao
/// comparaveis depois de rotacao ou reset.
/// </param>
public sealed record DeviceSyncStateResponse(
    Guid DeviceId,
    int SourceEpoch,
    DateTime? SourceEpochChangedAt,
    string? LastBackfillStatus,
    string? LastBackfillFailure,
    DateTime? LastBackfillAt,
    IReadOnlyList<DeviceCursorState> Cursors,
    long? ReportedAccessLogs,
    long StoredAccessLogs,
    bool PossiblyMissing,
    int? ClockSkewSeconds,
    string ClockStatus);

/// <summary>Rotas de backfill: as do gateway, com fencing, e as do operador, com permissao e escopo.</summary>
public static class BackfillEndpoints
{
    public const int ClockToleranceSeconds = 120;

    private static readonly string[] Objects = [EdgeEventFamilies.AccessLogs, EdgeEventFamilies.AlarmLogs, EdgeEventFamilies.AccessEvents];
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JobValidity = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapBackfillEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/devices/{id:guid}/backfill", RequestAsync)
            .WithTags("Devices").Produces(StatusCodes.Status202Accepted).RequirePermission(Permissions.DeviceBackfill).RequireIdempotency();
        routes.MapGet("/api/v1/devices/{id:guid}/sync-state", SyncStateAsync)
            .WithTags("Devices").Produces<DeviceSyncStateResponse>().RequirePermission(Permissions.DeviceRead);
        return routes;
    }

    internal static string ClockStatus(int? skewSeconds) => skewSeconds switch
    {
        null => "unknown",
        var s when Math.Abs(s.Value) > ClockToleranceSeconds => "skewed",
        _ => "ok",
    };

    internal static async Task<EdgeBackfillPlan> BuildPlanAsync(TenantTransaction tx, Guid deviceId)
    {
        var epoch = await tx.QuerySingleOrDefaultAsync<int>("SELECT source_epoch FROM hub.devices WHERE id = @deviceId", new { deviceId });
        var saved = (await tx.QueryAsync<CursorRow>(
                "SELECT object_name AS ObjectName, last_id::text AS LastId, last_time_unix AS LastTimeUnix FROM hub.device_event_cursors WHERE device_id = @deviceId AND source_epoch = @epoch",
                new { deviceId, epoch }))
            .ToDictionary(c => c.ObjectName);

        return new EdgeBackfillPlan(epoch, Objects.Select(name =>
            saved.TryGetValue(name, out var row)
                ? new EdgeBackfillCursor(name, row.LastId, row.LastTimeUnix)
                : new EdgeBackfillCursor(name, null, null)).ToList());
    }

    /// <summary>
    /// Agenda o que estiver vencido deste gateway: sem credencial ou sem identidade verificada nao ha o que tentar.
    /// Um comando pendente por aparelho; continuacao de leitura incompleta nao espera o intervalo.
    /// </summary>
    internal static Task ScheduleDueAsync(TenantTransaction tx, Guid gatewayId, TimeSpan interval) =>
        tx.ExecuteAsync(
            """
            INSERT INTO hub.commands (tenant_id, device_id, kind, idempotency_key, gateway_id, retryable, expires_at)
            SELECT d.tenant_id, d.id, @kind, gen_random_uuid()::text, d.gateway_id, true, now() + @validity
            FROM hub.devices d
            LEFT JOIN LATERAL (
              SELECT c.status, c.created_at, c.result FROM hub.commands c
              WHERE c.tenant_id = d.tenant_id AND c.device_id = d.id AND c.kind = @kind
              ORDER BY c.created_at DESC LIMIT 1) last ON true
            WHERE d.gateway_id = @gatewayId AND d.secret_ref IS NOT NULL AND d.source_device_id IS NOT NULL
              AND (last.status IS NULL
                   OR (last.status NOT IN ('queued', 'leased')
                       AND (last.created_at < now() - @interval OR (last.result ->> 'hasMore')::boolean IS TRUE)))
            """,
            new { kind = EdgeJobKinds.DeviceBackfill, gatewayId, interval, validity = JobValidity });

    internal static async Task<IResult> CheckpointAsync(Guid id, EdgeCheckpointRequest body, HttpContext http, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!Objects.Contains(body.ObjectName) || body.LastId is not { Length: > 0 and <= 20 } last || !last.All(char.IsAsciiDigit))
        {
            return Problems.Invalid("Checkpoint inválido.");
        }

        var gatewayId = http.GatewayOf();
        return await database.InTenantAsync<IResult>(
            http.TenantOf(),
            async tx =>
            {
                var command = await LeasedCommandAsync(tx, id, gatewayId, body.FencingToken);
                if (command is null)
                {
                    return Problems.Conflict("Este lease não é mais válido.");
                }

                var epoch = await tx.QuerySingleOrDefaultAsync<int>("SELECT source_epoch FROM hub.devices WHERE id = @command", new { command });

                // So para frente: um gateway com leitura antiga nao recua o cursor.
                await tx.ExecuteAsync(
                    """
                    INSERT INTO hub.device_event_cursors AS c (tenant_id, device_id, object_name, source_epoch, last_id, last_time_unix)
                    VALUES (@TenantId, @command, @ObjectName, @epoch, @last::numeric, @LastTimeUnix)
                    ON CONFLICT (tenant_id, device_id, object_name, source_epoch) DO UPDATE
                      SET last_id = excluded.last_id, last_time_unix = excluded.last_time_unix, updated_at = now()
                      WHERE excluded.last_id > c.last_id
                    """,
                    new { tx.TenantId, command, body.ObjectName, epoch, last, body.LastTimeUnix });

                await tx.ExecuteAsync("UPDATE hub.commands SET lease_until = now() + @lease WHERE id = @id", new { id, lease = Lease });
                return Results.NoContent();
            },
            cancellationToken);
    }

    internal static async Task<IResult> ResetEpochAsync(Guid id, EdgeEpochResetRequest body, HttpContext http, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var gatewayId = http.GatewayOf();
        return await database.InTenantAsync<IResult>(
            http.TenantOf(),
            async tx =>
            {
                var device = await LeasedCommandAsync(tx, id, gatewayId, body.FencingToken);
                if (device is null)
                {
                    return Problems.Conflict("Este lease não é mais válido.");
                }

                // Condicional: dois pedidos da mesma leitura abrem uma so epoca.
                var changed = await tx.ExecuteAsync(
                    "UPDATE hub.devices SET source_epoch = source_epoch + 1, source_epoch_changed_at = now() WHERE id = @device AND source_epoch = @ExpectedEpoch",
                    new { device, body.ExpectedEpoch });

                if (changed > 0)
                {
                    // Reset de aparelho e fato de seguranca: tambem pode ser troca do equipamento.
                    await AuditWriter.WriteAsync(tx, new AuditEntry(
                        null, "device.epoch_reset", "device", device.ToString(), AuditWriter.Success, http.GetCorrelationId(),
                        Metadata: new { previousEpoch = body.ExpectedEpoch, detectedBy = body.ObjectName, gateway = gatewayId }));
                }

                await tx.ExecuteAsync("UPDATE hub.commands SET lease_until = now() + @lease WHERE id = @id", new { id, lease = Lease });
                return Results.Ok(new EdgeEpochResetResponse(await BuildPlanAsync(tx, device.Value)));
            },
            cancellationToken);
    }

    private static Task<Guid?> LeasedCommandAsync(TenantTransaction tx, Guid id, Guid gatewayId, long fencing) =>
        tx.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT device_id FROM hub.commands WHERE id = @id AND gateway_id = @gatewayId AND fencing_token = @fencing AND status = 'leased' AND kind = @kind",
            new { id, gatewayId, fencing, kind = EdgeJobKinds.DeviceBackfill });

    private static async Task<IResult> RequestAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var device = await tx.QuerySingleOrDefaultAsync<RouteRow>("SELECT site_id AS SiteId, gateway_id AS GatewayId, source_device_id::text AS SourceDeviceId FROM hub.devices WHERE id = @id", new { id });
                if (device is null || !scope.Includes(device.SiteId))
                {
                    return Problems.NotFound();
                }

                if (device.GatewayId is null || device.SourceDeviceId is null)
                {
                    return Problems.Of(StatusCodes.Status422UnprocessableEntity, "device_not_verified", "Faça o teste de conexão antes de buscar os eventos deste equipamento.");
                }

                if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.commands WHERE device_id = @id AND kind = @kind AND status IN ('queued', 'leased') LIMIT 1", new { id, kind = EdgeJobKinds.DeviceBackfill }) is not null)
                {
                    return Problems.Conflict("Já existe uma busca de eventos em andamento neste equipamento.");
                }

                var job = await tx.QuerySingleOrDefaultAsync<Guid>(
                    """
                    INSERT INTO hub.commands (tenant_id, device_id, actor_id, kind, idempotency_key, gateway_id, retryable, expires_at)
                    VALUES (@TenantId, @id, @actor, @kind, gen_random_uuid()::text, @GatewayId, true, now() + @validity)
                    RETURNING id
                    """,
                    new { tx.TenantId, id, actor = access.Actor.ActorId, kind = EdgeJobKinds.DeviceBackfill, device.GatewayId, validity = JobValidity });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "device.backfill", "device", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { command = job }));
                return Results.Accepted($"/api/v1/jobs/{job}", new { id = job });
            },
            cancellationToken);
    }

    private static async Task<IResult> SyncStateAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var device = await tx.QuerySingleOrDefaultAsync<StateRow>(
                    """
                    SELECT site_id AS SiteId, source_epoch AS SourceEpoch, source_epoch_changed_at AS SourceEpochChangedAt,
                           reported_access_logs AS ReportedAccessLogs, clock_skew_seconds AS ClockSkewSeconds
                    FROM hub.devices WHERE id = @id
                    """,
                    new { id });

                if (device is null || !scope.Includes(device.SiteId))
                {
                    return Problems.NotFound();
                }

                var last = await tx.QuerySingleOrDefaultAsync<LastRow>(
                    "SELECT status AS Status, failure_code AS FailureCode, COALESCE(confirmed_at, created_at) AS At FROM hub.commands WHERE device_id = @id AND kind = @kind ORDER BY created_at DESC LIMIT 1",
                    new { id, kind = EdgeJobKinds.DeviceBackfill });

                var cursors = await tx.QueryAsync<DeviceCursorState>(
                    "SELECT object_name AS ObjectName, last_id::text AS LastId, updated_at AS UpdatedAt FROM hub.device_event_cursors WHERE device_id = @id AND source_epoch = @SourceEpoch ORDER BY object_name",
                    new { id, device.SourceEpoch });

                var stored = await tx.QuerySingleOrDefaultAsync<long>(
                    "SELECT count(*) FROM hub.access_events WHERE device_id = @id AND source_family = @family AND source_epoch = @SourceEpoch",
                    new { id, family = EdgeEventFamilies.AccessLogs, device.SourceEpoch });

                return Results.Ok(new DeviceSyncStateResponse(
                    id,
                    device.SourceEpoch,
                    device.SourceEpochChangedAt,
                    last?.Status,
                    last?.FailureCode,
                    last?.At,
                    cursors.ToList(),
                    device.ReportedAccessLogs,
                    stored,
                    device.ReportedAccessLogs > stored,
                    device.ClockSkewSeconds,
                    ClockStatus(device.ClockSkewSeconds)));
            },
            cancellationToken);
    }

    private sealed record CursorRow(string ObjectName, string LastId, long? LastTimeUnix);

    private sealed record RouteRow(Guid SiteId, Guid? GatewayId, string? SourceDeviceId);

    private sealed record StateRow(Guid SiteId, int SourceEpoch, DateTime? SourceEpochChangedAt, long? ReportedAccessLogs, int? ClockSkewSeconds);

    private sealed record LastRow(string Status, string? FailureCode, DateTime At);
}
