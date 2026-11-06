using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

namespace Platform.Modules.Fleet;

public sealed record GatewayResponse(Guid Id, Guid SiteId, string Name, string Status, DateTime? LastSeenAt, string? Version, int? SpoolDepth, int? ClockSkewSeconds);

public sealed record CreateGatewayRequest(string? Name);

public sealed record EnrollmentTokenResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record DeviceResponse(Guid Id, Guid SiteId, Guid? GatewayId, string Name, string? Model, string? FirmwareVersion, string? Host, int? Port, string TlsMode, string Status, DateTime? LastSeenAt, int? ClockSkewSeconds, bool HasCredential);

public sealed record CreateDeviceRequest(Guid SiteId, Guid GatewayId, string? Name, string? Model, string? Host, int? Port, string? TlsMode, string? TlsPin);

public sealed record DeviceCredentialRequest(string? Login, string? Password);

public sealed record CommandResponse(Guid Id, Guid DeviceId, string Kind, string Status, string? FailureCode, DateTime CreatedAt, DateTime? ConfirmedAt, JsonElement? Result);

/// <summary>Cadastro nao toca o equipamento (docs/12-contratos.md); so o teste de conexao, pela fila.</summary>
public static class FleetEndpoints
{
    private const string GatewayColumns =
        "id AS Id, site_id AS SiteId, name AS Name, status AS Status, last_seen_at AS LastSeenAt, version AS Version, spool_depth AS SpoolDepth, clock_skew_seconds AS ClockSkewSeconds";

    private const string DeviceColumns =
        """
        id AS Id, site_id AS SiteId, gateway_id AS GatewayId, name AS Name, model AS Model, firmware_version AS FirmwareVersion,
        host(endpoint_host) AS Host, endpoint_port AS Port, tls_mode AS TlsMode, status AS Status, last_seen_at AS LastSeenAt,
        clock_skew_seconds AS ClockSkewSeconds, secret_ref IS NOT NULL AS HasCredential
        """;

    private static readonly TimeSpan EnrollmentValidity = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProbeValidity = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapFleetEndpoints(this IEndpointRouteBuilder routes)
    {
        var gateways = routes.MapGroup("/api/v1/gateways").WithTags("Gateways");
        gateways.MapGet("/", ListGatewaysAsync).Produces<IEnumerable<GatewayResponse>>().RequirePermission(Permissions.GatewaysRead);
        gateways.MapPost("/", CreateGatewayAsync).Produces<GatewayResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.GatewaysManage).RequireIdempotency();

        // Sem idempotencia de proposito: a resposta traz o token, e ela ficaria guardada. Gerar outro invalida o anterior.
        gateways.MapPost("/{id:guid}/enrollment-token", CreateTokenAsync).Produces<EnrollmentTokenResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.GatewaysManage);
        gateways.MapPost("/{id:guid}/revoke", RevokeGatewayAsync).Produces(StatusCodes.Status204NoContent).RequireRateLimiting("sensitive").RequirePermission(Permissions.GatewaysManage).RequireIdempotency();

        var devices = routes.MapGroup("/api/v1/devices").WithTags("Devices");
        devices.MapGet("/", ListDevicesAsync).Produces<IEnumerable<DeviceResponse>>().RequirePermission(Permissions.DeviceRead);
        devices.MapGet("/{id:guid}", GetDeviceAsync).Produces<DeviceResponse>().RequirePermission(Permissions.DeviceRead);
        devices.MapPost("/", CreateDeviceAsync).Produces<DeviceResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.DeviceWrite).RequireIdempotency();

        // Sem idempotencia de proposito: o hash da requisicao guardado permitiria testar senhas fora do sistema.
        devices.MapPut("/{id:guid}/credential", SetCredentialAsync).Produces(StatusCodes.Status204NoContent).RequireRateLimiting("sensitive").RequirePermission(Permissions.DeviceSettingsWrite);
        devices.MapPost("/{id:guid}/probe", ProbeAsync).Produces<CommandResponse>(StatusCodes.Status202Accepted).RequirePermission(Permissions.DeviceProbe).RequireIdempotency();

        routes.MapGet("/api/v1/jobs/{id:guid}", GetJobAsync).WithTags("Jobs").Produces<CommandResponse>().RequirePermission(Permissions.DeviceRead);
        return routes;
    }

    private static object ScopeParameters(PermissionScope scope) =>
        new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() };

    private static async Task<IResult> ListGatewaysAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        Results.Ok(await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<GatewayResponse>(
                $"SELECT {GatewayColumns} FROM hub.edge_gateways WHERE (@all OR site_id = ANY(@allowed)) AND NOT site_id = ANY(@denied) ORDER BY name, id",
                ScopeParameters(http.GetScope())),
            cancellationToken));

    private static async Task<IResult> CreateGatewayAsync(CreateGatewayRequestWithSite body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return Problems.Invalid("Informe um nome de gateway com até 200 caracteres.");
        }

        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                {
                    return Problems.NotFound();
                }

                var gateway = await tx.QuerySingleOrDefaultAsync<GatewayResponse>(
                    $"INSERT INTO hub.edge_gateways (tenant_id, site_id, name) VALUES (@TenantId, @SiteId, @name) RETURNING {GatewayColumns}",
                    new { tx.TenantId, body.SiteId, name });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "gateway.create", "gateway", gateway!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.Created($"/api/v1/gateways/{gateway.Id}", gateway);
            },
            cancellationToken);
    }

    private static async Task<IResult> CreateTokenAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = DateTimeOffset.UtcNow.Add(EnrollmentValidity);

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var site = await tx.QuerySingleOrDefaultAsync<Guid?>("SELECT site_id FROM hub.edge_gateways WHERE id = @id AND status = 'pending'", new { id });
                if (site is not { } siteId || !scope.Includes(siteId))
                {
                    return Problems.NotFound();
                }

                await tx.ExecuteAsync("UPDATE hub.edge_enrollments SET superseded_at = now() WHERE gateway_id = @id AND used_at IS NULL AND superseded_at IS NULL", new { id });
                await tx.ExecuteAsync(
                    "INSERT INTO hub.edge_enrollments (tenant_id, gateway_id, token_hash, expires_at, created_by) VALUES (@TenantId, @id, @hash, @expires, @actor)",
                    new { tx.TenantId, id, hash = EdgeEndpoints.HashToken(token), expires = expires.UtcDateTime, actor = access.Actor.ActorId });
                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "gateway.enrollment_token", "gateway", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));

                return Results.Ok(new EnrollmentTokenResponse(token, expires));
            },
            cancellationToken);
    }

    private static async Task<IResult> RevokeGatewayAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var site = await tx.QuerySingleOrDefaultAsync<Guid?>("SELECT site_id FROM hub.edge_gateways WHERE id = @id AND status <> 'revoked'", new { id });
                if (site is not { } siteId || !scope.Includes(siteId))
                {
                    return Problems.NotFound();
                }

                await tx.ExecuteAsync("UPDATE hub.edge_gateways SET status = 'revoked', revoked_at = now() WHERE id = @id", new { id });
                await tx.ExecuteAsync("UPDATE hub.edge_enrollments SET superseded_at = now() WHERE gateway_id = @id AND used_at IS NULL AND superseded_at IS NULL", new { id });
                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "gateway.revoke", "gateway", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.NoContent();
            },
            cancellationToken);
    }

    private static async Task<IResult> ListDevicesAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        Results.Ok(await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<DeviceResponse>(
                $"SELECT {DeviceColumns} FROM hub.devices WHERE (@all OR site_id = ANY(@allowed)) AND NOT site_id = ANY(@denied) ORDER BY name, id",
                ScopeParameters(http.GetScope())),
            cancellationToken));

    private static async Task<IResult> GetDeviceAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var device = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QuerySingleOrDefaultAsync<DeviceResponse>($"SELECT {DeviceColumns} FROM hub.devices WHERE id = @id", new { id }),
            cancellationToken);

        return device is null || !http.GetScope().Includes(device.SiteId) ? Problems.NotFound() : Results.Ok(device);
    }

    private static async Task<IResult> CreateDeviceAsync(CreateDeviceRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        var problem = Validate(body);
        if (problem is not null)
        {
            return problem;
        }

        var name = body.Name!.Trim();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                // Gateway de outra unidade ou outro tenant nao alcanca este equipamento.
                if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.edge_gateways WHERE id = @GatewayId AND site_id = @SiteId AND status <> 'revoked'", new { body.GatewayId, body.SiteId }) is null)
                {
                    return Problems.NotFound();
                }

                var device = await tx.QuerySingleOrDefaultAsync<DeviceResponse>(
                    $"""
                    INSERT INTO hub.devices (tenant_id, site_id, gateway_id, name, model, endpoint_host, endpoint_port, tls_mode, tls_cert_thumbprint)
                    VALUES (@TenantId, @SiteId, @GatewayId, @name, @Model, @Host::inet, @Port, @TlsMode, @TlsPin)
                    RETURNING {DeviceColumns}
                    """,
                    new { tx.TenantId, body.SiteId, body.GatewayId, name, Model = body.Model?.Trim(), body.Host, body.Port, TlsMode = body.TlsMode ?? DeviceTlsModes.Pinned, body.TlsPin });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "device.create", "device", device!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.Created($"/api/v1/devices/{device.Id}", device);
            },
            cancellationToken);
    }

    private static IResult? Validate(CreateDeviceRequest body)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return Problems.Invalid("Informe um nome de equipamento com até 200 caracteres.");
        }

        if (!IPAddress.TryParse(body.Host, out _))
        {
            return Problems.Invalid("Informe o endereço IP do equipamento.");
        }

        if (body.Port is not (>= 1 and <= 65535))
        {
            return Problems.Invalid("Informe uma porta entre 1 e 65535.");
        }

        var mode = body.TlsMode ?? DeviceTlsModes.Pinned;
        if (mode is not (DeviceTlsModes.Pinned or DeviceTlsModes.System or DeviceTlsModes.PrivateCa or DeviceTlsModes.PlainHttp))
        {
            return Problems.Invalid("Modo de conexão desconhecido.");
        }

        var isPin = body.TlsPin is { Length: 64 } pin && pin.All(Uri.IsHexDigit);
        return mode == DeviceTlsModes.Pinned && !isPin
            ? Problems.Invalid("Informe o SHA-256 do certificado do equipamento, com 64 caracteres hexadecimais.")
            : null;
    }

    /// <summary>A senha entra aqui e nunca mais sai por esta API. So o gateway a recebe, no momento do comando.</summary>
    private static async Task<IResult> SetCredentialAsync(Guid id, DeviceCredentialRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(body.Login) || string.IsNullOrEmpty(body.Password) || body.Login.Length > 128 || body.Password.Length > 256)
        {
            return Problems.Invalid("Informe usuário e senha do equipamento.");
        }

        if (http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, "key_service_unavailable", "Serviço de chaves indisponível. A credencial não foi gravada.");
        }

        var tenant = access.Actor.TenantId;
        var device = await database.InTenantAsync(
            tenant,
            tx => tx.QuerySingleOrDefaultAsync<DeviceSecretRow>("SELECT site_id AS SiteId, secret_ref AS SecretRef FROM hub.devices WHERE id = @id", new { id }),
            cancellationToken);

        if (device is null || !http.GetScope().Includes(device.SiteId))
        {
            return Problems.NotFound();
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new EdgeDeviceLogin(body.Login, body.Password), EdgeJsonFormat.Options);
        BlobReference reference;
        try
        {
            using var plain = new MemoryStream(bytes);
            reference = await store.StoreAsync(tenant, BlobKind.DeviceSecret, BlobClassification.Secret, plain, access.Actor.ActorId, null, cancellationToken);
        }
        catch (KeyServiceException)
        {
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, "key_service_unavailable", "Serviço de chaves indisponível. A credencial não foi gravada.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        await database.InTenantAsync(
            tenant,
            async tx =>
            {
                await tx.ExecuteAsync("UPDATE hub.devices SET secret_ref = @secret WHERE id = @id", new { secret = reference.Id.ToString(), id });
                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "device.credential_set", "device", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return true;
            },
            cancellationToken);

        // Sem a chave, a credencial antiga deixa de existir de fato.
        if (Guid.TryParse(device.SecretRef, out var previous))
        {
            await store.DestroyAsync(tenant, previous, CancellationToken.None);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ProbeAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var device = await tx.QuerySingleOrDefaultAsync<DeviceRouteRow>("SELECT site_id AS SiteId, gateway_id AS GatewayId FROM hub.devices WHERE id = @id", new { id });
                if (device is null || !scope.Includes(device.SiteId))
                {
                    return Problems.NotFound();
                }

                if (device.GatewayId is null)
                {
                    return Problems.Of(StatusCodes.Status422UnprocessableEntity, "no_gateway", "Este equipamento não tem gateway associado.");
                }

                // Diagnostico so le: pode ser repetido se o gateway cair. Comando fisico tera retryable = false.
                var command = await tx.QuerySingleOrDefaultAsync<CommandRow>(
                    """
                    INSERT INTO hub.commands (tenant_id, device_id, actor_id, kind, idempotency_key, gateway_id, retryable, expires_at)
                    VALUES (@TenantId, @id, @actor, @kind, @key, @GatewayId, true, @expires)
                    RETURNING id AS Id, device_id AS DeviceId, kind AS Kind, status AS Status, failure_code AS FailureCode,
                              created_at AS CreatedAt, confirmed_at AS ConfirmedAt, result::text AS Result
                    """,
                    new
                    {
                        tx.TenantId,
                        id,
                        actor = access.Actor.ActorId,
                        kind = EdgeJobKinds.DeviceProbe,
                        key = Guid.NewGuid().ToString(),
                        device.GatewayId,
                        expires = DateTime.UtcNow.Add(ProbeValidity),
                    });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "device.probe", "device", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { command = command!.Id }));
                return Results.Accepted($"/api/v1/jobs/{command.Id}", ToResponse(command));
            },
            cancellationToken);
    }

    private static async Task<IResult> GetJobAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var row = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QuerySingleOrDefaultAsync<ScopedCommandRow>(
                """
                SELECT c.id AS Id, c.device_id AS DeviceId, c.kind AS Kind, c.status AS Status, c.failure_code AS FailureCode,
                       c.created_at AS CreatedAt, c.confirmed_at AS ConfirmedAt, c.result::text AS Result, d.site_id AS SiteId
                FROM hub.commands c JOIN hub.devices d ON d.tenant_id = c.tenant_id AND d.id = c.device_id
                WHERE c.id = @id
                """,
                new { id }),
            cancellationToken);

        return row is null || !scope.Includes(row.SiteId)
            ? Problems.NotFound()
            : Results.Ok(ToResponse(new CommandRow(row.Id, row.DeviceId, row.Kind, row.Status, row.FailureCode, row.CreatedAt, row.ConfirmedAt, row.Result)));
    }

    private static CommandResponse ToResponse(CommandRow row)
    {
        JsonElement? result = null;
        if (row.Result is not null)
        {
            using var document = JsonDocument.Parse(row.Result);
            result = document.RootElement.Clone();
        }

        return new CommandResponse(row.Id, row.DeviceId, row.Kind, row.Status, row.FailureCode, row.CreatedAt, row.ConfirmedAt, result);
    }

    public sealed record CreateGatewayRequestWithSite(Guid SiteId, string? Name);

    private sealed record DeviceSecretRow(Guid SiteId, string? SecretRef);

    private sealed record DeviceRouteRow(Guid SiteId, Guid? GatewayId);

    private sealed record CommandRow(Guid Id, Guid DeviceId, string Kind, string Status, string? FailureCode, DateTime CreatedAt, DateTime? ConfirmedAt, string? Result);

    private sealed record ScopedCommandRow(Guid Id, Guid DeviceId, string Kind, string Status, string? FailureCode, DateTime CreatedAt, DateTime? ConfirmedAt, string? Result, Guid SiteId);
}
