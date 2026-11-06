using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

namespace Platform.Modules.Fleet;

/// <summary>Rotas usadas pelo gateway. Autenticacao por certificado de cliente, nunca pela sessao de operador.</summary>
public static class EdgeEndpoints
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    public static IEndpointRouteBuilder MapEdgeEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/edge/v1").ExcludeFromDescription();

        group.MapPost("/enroll", EnrollAsync).AllowAnonymous().RequireRateLimiting("sensitive");

        var authenticated = group.MapGroup(string.Empty)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = EdgeIdentity.Scheme });
        authenticated.MapPost("/heartbeat", HeartbeatAsync);
        authenticated.MapGet("/devices", EdgeEventEndpoints.ListDevicesAsync);
        authenticated.MapPost("/events", EdgeEventEndpoints.ReceiveBatchAsync);
        authenticated.MapPost("/jobs/lease", LeaseAsync);
        authenticated.MapPost("/jobs/{id:guid}/result", ResultAsync);
        authenticated.MapPost("/jobs/{id:guid}/checkpoint", BackfillEndpoints.CheckpointAsync);
        authenticated.MapPost("/jobs/{id:guid}/epoch-reset", BackfillEndpoints.ResetEpochAsync);

        return routes;
    }

    private static IResult Refused() =>
        Problems.Of(StatusCodes.Status401Unauthorized, "enrollment_refused", "Registro do gateway recusado.");

    private static async Task<IResult> EnrollAsync(EnrollRequest body, HttpContext http, NpgsqlDataSource dataSource, ITenantDatabase database, CancellationToken cancellationToken)
    {
        // A chave vem do handshake: quem nao possui a chave privada nao chega aqui com ela.
        var certificate = await http.Connection.GetClientCertificateAsync(cancellationToken);
        if (certificate is null || !EdgeIdentity.IsWithinValidity(certificate) || string.IsNullOrEmpty(body.Token))
        {
            return Refused();
        }

        ClaimedGateway? claimed;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            claimed = await connection.QuerySingleOrDefaultAsync<ClaimedGateway>(
                "SELECT tenant_id AS TenantId, gateway_id AS GatewayId, site_id AS SiteId FROM hub.claim_enrollment(@hash, @thumbprint)",
                new { hash = HashToken(body.Token), thumbprint = EdgeIdentity.ThumbprintOf(certificate) });
        }

        // A mesma resposta para token invalido, usado ou vencido: nao ajuda a adivinhar.
        if (claimed is null)
        {
            return Refused();
        }

        await database.InTenantAsync(
            claimed.TenantId,
            async tx =>
            {
                await tx.ExecuteAsync("UPDATE hub.edge_gateways SET version = @Version WHERE id = @GatewayId", new { body.Version, claimed.GatewayId });
                await AuditWriter.WriteAsync(tx, new AuditEntry(null, "gateway.enroll", "gateway", claimed.GatewayId.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return true;
            },
            cancellationToken);

        return Results.Ok(new EnrollResponse(claimed.GatewayId, claimed.SiteId));
    }

    private static async Task<IResult> HeartbeatAsync(HeartbeatRequest body, HttpContext http, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var skew = (long)(body.GatewayClockUtc - now).TotalSeconds;
        var gatewayId = http.GatewayOf();

        await database.InTenantAsync(
            http.TenantOf(),
            tx => tx.ExecuteAsync(
                """
                UPDATE hub.edge_gateways
                SET last_seen_at = now(), version = @version, spool_depth = @SpoolDepth, clock_skew_seconds = @skew
                WHERE id = @gatewayId
                """,
                new { version = Limit(body.Version, 64), body.SpoolDepth, skew = (int)Math.Clamp(skew, int.MinValue, int.MaxValue), gatewayId }),
            cancellationToken);

        return Results.Ok(new HeartbeatResponse(now));
    }

    private static async Task<IResult> LeaseAsync(HttpContext http, ITenantDatabase database, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var backfillInterval = TimeSpan.FromSeconds(configuration.GetValue("Edge:BackfillIntervalSeconds", 900));
        var tenant = http.TenantOf();
        var gatewayId = http.GatewayOf();

        var row = await database.InTenantAsync(
            tenant,
            async tx =>
            {
                // Vencidos saem da fila. Comando nao repetivel com lease perdido fica "unknown": nao se sabe se executou.
                await tx.ExecuteAsync(
                    """
                    UPDATE hub.commands SET status = 'expired', failure_code = 'ttl_expired'
                    WHERE gateway_id = @gatewayId AND status = 'queued' AND expires_at <= now();

                    UPDATE hub.commands SET status = 'unknown', failure_code = 'lease_lost'
                    WHERE gateway_id = @gatewayId AND status = 'leased' AND lease_until <= now() AND NOT retryable;

                    UPDATE hub.commands SET status = 'failed', failure_code = 'attempts_exhausted'
                    WHERE gateway_id = @gatewayId AND status = 'leased' AND lease_until <= now() AND retryable AND attempts >= @MaxAttempts;

                    UPDATE hub.commands SET status = 'expired', failure_code = 'ttl_expired'
                    WHERE gateway_id = @gatewayId AND status = 'leased' AND lease_until <= now() AND retryable AND expires_at <= now();
                    """,
                    new { gatewayId, MaxAttempts });

                await BackfillEndpoints.ScheduleDueAsync(tx, gatewayId, backfillInterval);

                // Um comando por vez em cada equipamento (docs/02-arquitetura.md). O novo fencing invalida quem ficou para tras.
                return await tx.QuerySingleOrDefaultAsync<LeasedRow>(
                    """
                    WITH next AS (
                      SELECT c.id FROM hub.commands c
                      WHERE c.gateway_id = @gatewayId
                        AND (c.status = 'queued' OR (c.status = 'leased' AND c.retryable AND c.lease_until <= now()))
                        AND NOT EXISTS (
                          SELECT 1 FROM hub.commands other
                          WHERE other.device_id = c.device_id AND other.id <> c.id AND other.status = 'leased' AND other.lease_until > now())
                      ORDER BY c.created_at, c.id
                      LIMIT 1
                      FOR UPDATE OF c SKIP LOCKED)
                    UPDATE hub.commands c
                    SET status = 'leased', lease_until = now() + @lease, fencing_token = c.fencing_token + 1,
                        attempts = c.attempts + 1, issued_at = now()
                    FROM next, hub.devices d
                    WHERE c.id = next.id AND d.tenant_id = c.tenant_id AND d.id = c.device_id
                    RETURNING c.id AS Id, c.kind AS Kind, c.fencing_token AS FencingToken, c.lease_until AS LeaseUntil, c.expires_at AS ExpiresAt,
                              d.id AS DeviceId, host(d.endpoint_host) AS Host, d.endpoint_port AS Port, d.tls_mode AS TlsMode,
                              d.tls_cert_thumbprint AS TlsPin, d.secret_ref AS SecretRef
                    """,
                    new { gatewayId, lease = Lease });
            },
            cancellationToken);

        if (row is null)
        {
            return Results.Ok(new EdgeJobLease(null));
        }

        var plan = row.Kind == EdgeJobKinds.DeviceBackfill
            ? await database.InTenantAsync(tenant, tx => BackfillEndpoints.BuildPlanAsync(tx, row.DeviceId), cancellationToken)
            : null;
        var login = await ReadLoginAsync(http, tenant, row.SecretRef, cancellationToken);
        var target = new EdgeDeviceTarget(row.DeviceId, row.Host ?? string.Empty, row.Port ?? 0, row.TlsMode, row.TlsPin, login);
        var job = new EdgeJob(
            row.Id,
            row.Kind,
            row.FencingToken,
            new DateTimeOffset(row.LeaseUntil, TimeSpan.Zero),
            row.ExpiresAt is { } expires ? new DateTimeOffset(expires, TimeSpan.Zero) : null,
            target,
            plan);

        return Results.Ok(new EdgeJobLease(job));
    }

    private static async Task<IResult> ResultAsync(Guid id, EdgeJobResult body, HttpContext http, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.Outcome is not (EdgeJobOutcomes.Succeeded or EdgeJobOutcomes.Failed))
        {
            return Problems.Invalid("Resultado inválido.");
        }

        var gatewayId = http.GatewayOf();
        var status = body.Outcome == EdgeJobOutcomes.Succeeded ? "completed" : "failed";

        return await database.InTenantAsync<IResult>(
            http.TenantOf(),
            async tx =>
            {
                // Tambem aceita "unknown": a resposta atrasada e justamente o que faltava saber. Fencing antigo e recusado.
                var updated = await tx.QuerySingleOrDefaultAsync<UpdatedCommand>(
                    """
                    UPDATE hub.commands
                    SET status = @status, result_kind = @Outcome, failure_code = @failure, result = @result::jsonb, confirmed_at = now(), lease_until = NULL
                    WHERE id = @id AND gateway_id = @gatewayId AND fencing_token = @FencingToken AND status IN ('leased', 'unknown')
                    RETURNING device_id AS DeviceId, actor_id AS ActorId, kind AS Kind
                    """,
                    new
                    {
                        status,
                        body.Outcome,
                        failure = Limit(body.FailureCode, 64),
                        result = ResultJson(body),
                        id,
                        gatewayId,
                        body.FencingToken,
                    });

                if (updated is null)
                {
                    // Repetir o mesmo resultado nao e erro; so o fencing errado ou comando alheio e.
                    var already = await tx.QuerySingleOrDefaultAsync<int?>(
                        "SELECT 1 FROM hub.commands WHERE id = @id AND gateway_id = @gatewayId AND fencing_token = @FencingToken AND status IN ('completed', 'failed')",
                        new { id, gatewayId, body.FencingToken });
                    return already is null
                        ? Problems.Conflict("Este lease não é mais válido.")
                        : Results.NoContent();
                }

                if (body.Backfill is { ClockSkewSeconds: { } measured })
                {
                    await tx.ExecuteAsync(
                        "UPDATE hub.devices SET clock_skew_seconds = @skew, last_seen_at = now(), status = 'reachable' WHERE id = @DeviceId",
                        new { skew = (int)Math.Clamp(measured, int.MinValue, int.MaxValue), updated.DeviceId });
                }

                if (body.Probe is { } probe)
                {
                    await tx.ExecuteAsync(
                        """
                        UPDATE hub.devices
                        SET firmware_version = COALESCE(@Firmware, firmware_version),
                            clock_skew_seconds = COALESCE(@skew, clock_skew_seconds),
                            status = @deviceStatus,
                            last_seen_at = CASE WHEN @reached THEN now() ELSE last_seen_at END,
                            source_device_id = CASE
                              WHEN source_device_id IS NULL AND @source::numeric IS NOT NULL
                                AND NOT EXISTS (SELECT 1 FROM hub.devices other WHERE other.site_id = hub.devices.site_id AND other.source_device_id = @source::numeric)
                              THEN @source::numeric ELSE source_device_id END
                        WHERE id = @DeviceId
                        """,
                        new
                        {
                            Firmware = Limit(probe.Firmware, 64),
                            skew = probe.ClockSkewSeconds is { } s ? (int?)Math.Clamp(s, int.MinValue, int.MaxValue) : null,
                            deviceStatus = probe.Reachability == "Reachable" ? "reachable" : "unreachable",
                            reached = probe.Reachability == "Reachable",

                            // So com autenticacao valida: resposta de quem nao passou no login nao ensina identidade.
                            source = probe.Authentication == "Valid" && probe.SourceDeviceId is { Length: > 0 and <= 20 } id && id.All(char.IsAsciiDigit) ? id : null,
                            updated.DeviceId,
                        });
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    updated.ActorId, updated.Kind + ".result", "device", updated.DeviceId.ToString(), body.Outcome == EdgeJobOutcomes.Succeeded ? AuditWriter.Success : "failed", http.GetCorrelationId(),
                    Metadata: new { command = id, failure = Limit(body.FailureCode, 64) }));

                return Results.NoContent();
            },
            cancellationToken);
    }

    private static async Task<EdgeDeviceLogin?> ReadLoginAsync(HttpContext http, Guid tenant, string? secretRef, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(secretRef, out var blobId) || http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return null;
        }

        using var plain = new MemoryStream();
        try
        {
            if (!await store.ReadAsync(tenant, blobId, plain, cancellationToken))
            {
                return null;
            }

            return JsonSerializer.Deserialize<EdgeDeviceLogin>(plain.GetBuffer().AsSpan(0, (int)plain.Length), EdgeJsonFormat.Options);
        }
        catch (Exception ex) when (ex is KeyServiceException or BlobIntegrityException or JsonException)
        {
            // O gateway relata falta de credencial; o motivo fica fora da resposta.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain.GetBuffer());
        }
    }

    private static string? ResultJson(EdgeJobResult body)
    {
        object? report = (object?)body.Probe ?? body.Backfill;
        return report is null ? null : JsonSerializer.Serialize(report, report.GetType(), EdgeJsonFormat.Options);
    }

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string? Limit(string? value, int length) =>
        value is { Length: > 0 } text ? (text.Length <= length ? text : text[..length]) : null;

    private sealed record ClaimedGateway(Guid TenantId, Guid GatewayId, Guid SiteId);

    private sealed record UpdatedCommand(Guid DeviceId, Guid? ActorId, string Kind);

    private sealed record LeasedRow(
        Guid Id,
        string Kind,
        long FencingToken,
        DateTime LeaseUntil,
        DateTime? ExpiresAt,
        Guid DeviceId,
        string? Host,
        int? Port,
        string TlsMode,
        string? TlsPin,
        string? SecretRef);
}

internal static class EdgeJsonFormat
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
