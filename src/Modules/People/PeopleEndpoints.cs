using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

namespace Platform.Modules.People;

public sealed record CreatePersonRequest(Guid SiteId, string? DisplayName, string? Registration, string? PersonType, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, string? SourceSystem, string? ExternalKey);

/// <summary>Campo ausente fica como esta. Para limpar vigencia, use ClearValidFrom e ClearValidTo.</summary>
public sealed record UpdatePersonRequest(string? DisplayName, string? Registration, string? PersonType, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, bool ClearValidFrom, bool ClearValidTo);

public sealed record RevokePersonRequest(string? Reason);

public sealed record PersonResponse(
    Guid Id,
    Guid? SiteId,
    string DisplayName,
    string? Registration,
    bool RegistrationMasked,
    string PersonType,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    string State,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? RevokedAt,
    string? RevokeReason);

public sealed record PersonPageResponse(IReadOnlyList<PersonResponse> Items, int? NextOffset);

public sealed record CreateCredentialRequest(string? Kind, string? Value, DateTimeOffset? ValidUntil);

public sealed record RevokeCredentialRequest(string? Reason);

public sealed record CredentialLookupRequest(string? Kind, string? Value);

public sealed record CredentialResponse(Guid Id, Guid PersonId, string Kind, string? Hint, DateTime? ValidUntil, DateTime? RevokedAt, string? RevokeReason, DateTime CreatedAt);

public sealed record CredentialLookupResponse(bool Found, Guid? PersonId);

public sealed record LinkDeviceRequest(Guid DeviceId, string? SourceUserId);

public sealed record DeviceLinkResponse(Guid DeviceId, string DeviceName, string SourceUserId, int SourceEpoch, DateTime? ObservedAt);

public sealed record SourceKeyResponse(string SourceSystem, string ExternalKey, DateTime CreatedAt);

/// <summary>
/// Cadastro canonico. O ID interno nao depende do ID no terminal (RF-006). Nada aqui fala com equipamento: mudancas viram
/// evento no outbox, e quem envia e a sincronizacao. Excluir pessoa nao existe nesta rota: revogar e o caminho auditado.
/// </summary>
public static class PeopleEndpoints
{
    private const string Columns =
        """
        id AS Id, site_id AS SiteId, display_name AS DisplayName, registration AS Registration, person_type AS PersonType, valid_from AS ValidFrom, valid_to AS ValidTo,
        state AS State, version AS Version, created_at AS CreatedAt, updated_at AS UpdatedAt, revoked_at AS RevokedAt, revoke_reason AS RevokeReason
        """;

    private const string CredentialColumns =
        "id AS Id, person_id AS PersonId, kind AS Kind, hint AS Hint, valid_until AS ValidUntil, revoked_at AS RevokedAt, revoke_reason AS RevokeReason, created_at AS CreatedAt";

    private const int MaxName = 200;
    private const int MaxRegistration = 64;
    private const int MaxReason = 500;
    private const int MaxPage = 100;
    private const int MaxOffset = 10_000;
    private static readonly string[] PersonTypes = ["employee", "visitor", "contractor", "other"];

    public static IEndpointRouteBuilder MapPeopleEndpoints(this IEndpointRouteBuilder routes)
    {
        var people = routes.MapGroup("/api/v1/people").WithTags("People");
        people.MapGet("/", ListAsync).Produces<PersonPageResponse>().RequirePermission(Permissions.PeopleRead);
        people.MapPost("/", CreateAsync).Produces<PersonResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PeopleWrite).RequireIdempotency();
        people.MapGet("/{id:guid}", GetAsync).Produces<PersonResponse>().RequirePermission(Permissions.PeopleRead);
        people.MapPatch("/{id:guid}", UpdateAsync).Produces<PersonResponse>().RequirePermission(Permissions.PeopleWrite).RequireIdempotency();
        people.MapPost("/{id:guid}/revoke", RevokeAsync).Produces<PersonResponse>().RequirePermission(Permissions.PeopleRevoke).RequireIdempotency();

        people.MapGet("/{id:guid}/device-links", DeviceLinksAsync).Produces<IEnumerable<DeviceLinkResponse>>().RequirePermission(Permissions.PeopleRead);
        people.MapPost("/{id:guid}/device-links", LinkDeviceAsync).Produces<DeviceLinkResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PeopleWrite).RequireIdempotency();
        people.MapGet("/{id:guid}/source-keys", SourceKeysAsync).Produces<IEnumerable<SourceKeyResponse>>().RequirePermission(Permissions.PeopleRead);

        // Sem idempotencia por chave nas rotas com valor: o hash do corpo guardaria algo derivado de um PIN. Protege o indice unico.
        people.MapGet("/{id:guid}/credentials", CredentialsAsync).Produces<IEnumerable<CredentialResponse>>().RequirePermission(Permissions.CredentialsManage);
        people.MapPost("/{id:guid}/credentials", AddCredentialAsync).Produces<CredentialResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.CredentialsManage);
        people.MapPost("/{id:guid}/credentials/{credentialId:guid}/revoke", RevokeCredentialAsync).Produces<CredentialResponse>().RequirePermission(Permissions.CredentialsManage).RequireIdempotency();

        routes.MapPost("/api/v1/credentials/lookup", LookupAsync).WithTags("People").Produces<CredentialLookupResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.CredentialsManage);
        return routes;
    }

    private static async Task<IResult> ListAsync(string? search, int? offset, int? limit, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var take = Math.Clamp(limit ?? 50, 1, MaxPage);
        var skip = Math.Clamp(offset ?? 0, 0, MaxOffset);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (term is { Length: > 200 })
        {
            return Problems.Invalid("Busca longa demais.");
        }

        var writer = await access.GetScopeAsync(Permissions.PeopleWrite, cancellationToken);
        var rows = (await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<PersonRow>(
                $"""
                SELECT {Columns} FROM hub.people
                WHERE deleted_at IS NULL
                  AND (@all OR site_id = ANY(@allowed) OR site_id IS NULL) AND (site_id IS NULL OR NOT site_id = ANY(@denied))
                  AND (@term::text IS NULL OR lower(display_name) LIKE @prefix ESCAPE '\' OR registration = @term)
                ORDER BY lower(display_name), id
                OFFSET @skip LIMIT @take
                """,
                new
                {
                    all = scope.AllSites,
                    allowed = scope.AllowedSites.ToArray(),
                    denied = scope.DeniedSites.ToArray(),
                    term,
                    prefix = term is null ? null : EscapeLike(term.ToLower(CultureInfo.InvariantCulture)) + "%",
                    skip,
                    take = take + 1,
                }),
            cancellationToken)).ToList();

        var page = rows.Take(take).Select(row => ToResponse(row, writer)).ToList();
        return Results.Ok(new PersonPageResponse(page, rows.Count > take && skip + take <= MaxOffset ? skip + take : null));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var person = await database.InTenantAsync(access.Actor.TenantId, tx => FindAsync(tx, id), cancellationToken);
        if (person is null || !InScope(http, person))
        {
            return Problems.NotFound();
        }

        http.Response.Headers.ETag = $"\"{person.Version}\"";
        return Results.Ok(ToResponse(person, await access.GetScopeAsync(Permissions.PeopleWrite, cancellationToken)));
    }

    private static async Task<IResult> CreateAsync(CreatePersonRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if (Validate(body.DisplayName, body.Registration, body.PersonType ?? "employee", body.ValidFrom, body.ValidTo) is { } problem)
        {
            return Problems.Invalid(problem);
        }

        if ((body.SourceSystem is null) != (body.ExternalKey is null) || body.SourceSystem is { Length: 0 or > 64 } || body.ExternalKey is { Length: 0 or > 128 })
        {
            return Problems.Invalid("Informe o sistema de origem e a chave externa juntos.");
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                    {
                        return Problems.NotFound();
                    }

                    var person = await tx.QuerySingleOrDefaultAsync<PersonRow>(
                        $"""
                        INSERT INTO hub.people (tenant_id, site_id, display_name, registration, person_type, valid_from, valid_to)
                        VALUES (@TenantId, @SiteId, @name, @registration, @type, @from, @to)
                        RETURNING {Columns}
                        """,
                        new { tx.TenantId, body.SiteId, name = body.DisplayName!.Trim(), registration = body.Registration, type = body.PersonType ?? "employee", from = Utc(body.ValidFrom), to = Utc(body.ValidTo) });

                    if (body.SourceSystem is not null)
                    {
                        await tx.ExecuteAsync(
                            "INSERT INTO hub.entity_source_keys (tenant_id, entity_type, source_system, external_key, entity_id, created_by) VALUES (@TenantId, 'person', @system, @key, @id, @actor)",
                            new { tx.TenantId, system = body.SourceSystem, key = body.ExternalKey, id = person!.Id, actor = access.Actor.ActorId });
                    }

                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "person.create", "person", person!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    await OutboxWriter.EnqueueAsync(tx, "person.created.v1", person.Id, http.GetCorrelationId());
                    return Results.Created($"/api/v1/people/{person.Id}", ToResponse(person, http.GetScope()));
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "registration_in_use", "Esta matrícula ou chave externa já está em uso nesta unidade.");
        }
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdatePersonRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!TryVersion(http, out var expected))
        {
            return Problems.Of(StatusCodes.Status428PreconditionRequired, "version_required", "Informe a versão que você está editando no cabeçalho If-Match.");
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    var current = await FindAsync(tx, id);
                    if (current is null || !InScope(http, current))
                    {
                        return Problems.NotFound();
                    }

                    if (current.State == "revoked")
                    {
                        return Problems.Conflict("Esta pessoa está revogada e não pode ser editada.");
                    }

                    var name = body.DisplayName ?? current.DisplayName;
                    var registration = body.Registration ?? current.Registration;
                    var type = body.PersonType ?? current.PersonType;
                    DateTimeOffset? from = body.ClearValidFrom ? null : body.ValidFrom ?? Offset(current.ValidFrom);
                    DateTimeOffset? to = body.ClearValidTo ? null : body.ValidTo ?? Offset(current.ValidTo);

                    if (Validate(name, registration, type, from, to) is { } problem)
                    {
                        return Problems.Invalid(problem);
                    }

                    var updated = await tx.QuerySingleOrDefaultAsync<PersonRow>(
                        $"""
                        UPDATE hub.people
                        SET display_name = @name, registration = @registration, person_type = @type, valid_from = @from, valid_to = @to, version = version + 1, updated_at = now()
                        WHERE id = @id AND version = @expected
                        RETURNING {Columns}
                        """,
                        new { name = name.Trim(), registration, type, from = Utc(from), to = Utc(to), id, expected });

                    if (updated is null)
                    {
                        return Problems.Of(StatusCodes.Status412PreconditionFailed, "version_conflict", "Outra pessoa alterou este cadastro. Abra de novo para ver a versão atual.");
                    }

                    // So os nomes dos campos: o valor antigo e o novo sao dados pessoais.
                    var changed = new[]
                    {
                        name.Trim() != current.DisplayName ? "display_name" : null,
                        registration != current.Registration ? "registration" : null,
                        type != current.PersonType ? "person_type" : null,
                        Utc(from) != current.ValidFrom ? "valid_from" : null,
                        Utc(to) != current.ValidTo ? "valid_to" : null,
                    }.Where(field => field is not null).ToArray();

                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "person.update", "person", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { changed }));
                    await OutboxWriter.EnqueueAsync(tx, "person.updated.v1", id, http.GetCorrelationId());
                    http.Response.Headers.ETag = $"\"{updated.Version}\"";
                    return Results.Ok(ToResponse(updated, http.GetScope()));
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "registration_in_use", "Esta matrícula já está em uso nesta unidade.");
        }
    }

    /// <summary>Revogar tambem encerra as credenciais ativas, na mesma transacao. Levar aos equipamentos e da sincronizacao.</summary>
    private static async Task<IResult> RevokeAsync(Guid id, RevokePersonRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > MaxReason)
        {
            return Problems.Invalid($"Informe o motivo da revogação, com até {MaxReason} caracteres.");
        }

        var reason = body.Reason.Trim();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var current = await FindAsync(tx, id);
                if (current is null || !InScope(http, current))
                {
                    return Problems.NotFound();
                }

                var updated = await tx.QuerySingleOrDefaultAsync<PersonRow>(
                    $"""
                    UPDATE hub.people
                    SET state = 'revoked', revoked_at = now(), revoked_by = @actor, revoke_reason = @reason, version = version + 1, updated_at = now()
                    WHERE id = @id AND state <> 'revoked'
                    RETURNING {Columns}
                    """,
                    new { actor = access.Actor.ActorId, reason, id });

                if (updated is null)
                {
                    return Problems.Conflict("Esta pessoa já está revogada.");
                }

                var credentials = await tx.ExecuteAsync(
                    "UPDATE hub.credentials SET revoked_at = now(), revoked_by = @actor, revoke_reason = 'person_revoked' WHERE person_id = @id AND revoked_at IS NULL",
                    new { actor = access.Actor.ActorId, id });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "person.revoke", "person", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Reason: reason, Metadata: new { credentials }));
                await OutboxWriter.EnqueueAsync(tx, "person.revoked.v1", id, http.GetCorrelationId());
                return Results.Ok(ToResponse(updated, await WriterScopeAsync(access, http, cancellationToken)));
            },
            cancellationToken);
    }

    private static Task<PermissionScope> WriterScopeAsync(RequestAccess access, HttpContext http, CancellationToken cancellationToken) =>
        access.GetScopeAsync(Permissions.PeopleWrite, http.RequestAborted.IsCancellationRequested ? cancellationToken : http.RequestAborted);

    private static async Task<IResult> DeviceLinksAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var person = await FindAsync(tx, id);
                if (person is null || !InScope(http, person))
                {
                    return Problems.NotFound();
                }

                var links = await tx.QueryAsync<DeviceLinkResponse>(
                    """
                    SELECT l.device_id AS DeviceId, d.name AS DeviceName, l.source_user_id::text AS SourceUserId, l.source_epoch AS SourceEpoch, l.observed_at AS ObservedAt
                    FROM hub.person_device_links l JOIN hub.devices d ON d.tenant_id = l.tenant_id AND d.id = l.device_id
                    WHERE l.person_id = @id
                    ORDER BY d.name, l.device_id
                    """,
                    new { id });
                return Results.Ok(links);
            },
            cancellationToken);

    /// <summary>Vinculo com o usuario no terminal (RF-007). O ID e texto exato e so vale na epoca atual do equipamento.</summary>
    private static async Task<IResult> LinkDeviceAsync(Guid id, LinkDeviceRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.SourceUserId is not { Length: > 0 and <= 20 } source || !source.All(char.IsAsciiDigit))
        {
            return Problems.Invalid("O ID no equipamento deve ser numérico.");
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    var person = await FindAsync(tx, id);
                    if (person is null || !InScope(http, person))
                    {
                        return Problems.NotFound();
                    }

                    // Equipamento de outra unidade nao aparece como opcao, nem por tentativa.
                    var device = await tx.QuerySingleOrDefaultAsync<LinkedDevice>(
                        "SELECT name AS Name, source_epoch AS SourceEpoch, site_id AS SiteId FROM hub.devices WHERE id = @DeviceId",
                        new { body.DeviceId });
                    if (device is null || (person.SiteId is { } site && device.SiteId != site) || !http.GetScope().Includes(device.SiteId))
                    {
                        return Problems.NotFound();
                    }

                    await tx.ExecuteAsync(
                        "INSERT INTO hub.person_device_links (tenant_id, person_id, device_id, source_user_id, source_epoch) VALUES (@TenantId, @id, @DeviceId, @source::numeric, @SourceEpoch)",
                        new { tx.TenantId, id, body.DeviceId, source, device.SourceEpoch });
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "person.device_link", "person", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { device = body.DeviceId }));
                    return Results.Created($"/api/v1/people/{id}/device-links", new DeviceLinkResponse(body.DeviceId, device.Name, source, device.SourceEpoch, null));
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "link_in_use", "Esta pessoa já tem vínculo com o equipamento, ou o ID já pertence a outra pessoa.");
        }
    }

    private static async Task<IResult> SourceKeysAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var person = await FindAsync(tx, id);
                if (person is null || !InScope(http, person))
                {
                    return Problems.NotFound();
                }

                return Results.Ok(await tx.QueryAsync<SourceKeyResponse>(
                    "SELECT source_system AS SourceSystem, external_key AS ExternalKey, created_at AS CreatedAt FROM hub.entity_source_keys WHERE entity_type = 'person' AND entity_id = @id ORDER BY source_system",
                    new { id }));
            },
            cancellationToken);

    private static async Task<IResult> CredentialsAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var person = await FindAsync(tx, id);
                if (person is null || !InScope(http, person))
                {
                    return Problems.NotFound();
                }

                // Biometria tem permissao e fluxo proprios: nem o tipo aparece aqui.
                return Results.Ok(await tx.QueryAsync<CredentialResponse>(
                    $"SELECT {CredentialColumns} FROM hub.credentials WHERE person_id = @id AND kind = ANY(@kinds) ORDER BY created_at DESC, id",
                    new { id, kinds = new[] { CredentialKinds.Card, CredentialKinds.Qr, CredentialKinds.Uhf, CredentialKinds.Pin } }));
            },
            cancellationToken);

    private static async Task<IResult> AddCredentialAsync(Guid id, CreateCredentialRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!CredentialKinds.IsSupported(body.Kind))
        {
            return Problems.Invalid("Tipo de credencial inválido. Biometria tem fluxo próprio.");
        }

        var kind = body.Kind!;
        if (!CredentialValue.TryNormalize(kind, body.Value, out var normalized))
        {
            return Problems.Invalid(kind == CredentialKinds.Pin ? "O PIN deve ter de 4 a 16 dígitos." : "O valor da credencial é inválido.");
        }

        if (http.RequestServices.GetService<CredentialProtector>() is not { } protector)
        {
            return Unavailable();
        }

        var credentialId = Guid.NewGuid();
        var tenant = access.Actor.TenantId;
        var person = await database.InTenantAsync(tenant, tx => FindAsync(tx, id), cancellationToken);
        if (person is null || !InScope(http, person))
        {
            return Problems.NotFound();
        }

        if (person.State == "revoked")
        {
            return Problems.Conflict("Esta pessoa está revogada e não pode receber credenciais.");
        }

        ProtectedCredential sealedValue;
        byte[]? index;
        try
        {
            // Fora da transacao: nao segura conexao esperando o KMS.
            sealedValue = await protector.ProtectAsync(tenant, credentialId, kind, normalized, cancellationToken);
            index = CredentialKinds.IsSearchable(kind) ? await protector.IndexAsync(tenant, kind, normalized, cancellationToken) : null;
        }
        catch (Exception ex) when (ex is KeyServiceException or HttpRequestException)
        {
            return Unavailable();
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                tenant,
                async tx =>
                {
                    var created = await tx.QuerySingleOrDefaultAsync<CredentialResponse>(
                        $"""
                        INSERT INTO hub.credentials (id, tenant_id, person_id, kind, ciphertext, key_ref, blind_index, hint, valid_until, created_by)
                        VALUES (@credentialId, @TenantId, @id, @kind, @ciphertext, @key, @index, @hint, @until, @actor)
                        RETURNING {CredentialColumns}
                        """,
                        new
                        {
                            credentialId,
                            tx.TenantId,
                            id,
                            kind,
                            ciphertext = sealedValue.Ciphertext,
                            key = sealedValue.WrappedKey,
                            index,
                            hint = CredentialValue.Hint(kind, normalized),
                            until = Utc(body.ValidUntil),
                            actor = access.Actor.ActorId,
                        });
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "credential.create", "credential", credentialId.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { kind, person = id }));
                    await OutboxWriter.EnqueueAsync(tx, "person.updated.v1", id, http.GetCorrelationId());
                    return Results.Created($"/api/v1/people/{id}/credentials/{credentialId}", created);
                },
                CancellationToken.None);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Nao diz de quem e: o operador pode nem ter acesso a pessoa dona do cartao.
            return Problems.Of(StatusCodes.Status409Conflict, "credential_in_use", kind == CredentialKinds.Pin ? "Esta pessoa já tem um PIN ativo. Revogue-o antes." : "Esta credencial já está em uso.");
        }
    }

    private static async Task<IResult> RevokeCredentialAsync(Guid id, Guid credentialId, RevokeCredentialRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > MaxReason)
        {
            return Problems.Invalid($"Informe o motivo, com até {MaxReason} caracteres.");
        }

        var reason = body.Reason.Trim();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var person = await FindAsync(tx, id);
                if (person is null || !InScope(http, person))
                {
                    return Problems.NotFound();
                }

                var updated = await tx.QuerySingleOrDefaultAsync<CredentialResponse>(
                    $"""
                    UPDATE hub.credentials SET revoked_at = now(), revoked_by = @actor, revoke_reason = @reason
                    WHERE id = @credentialId AND person_id = @id AND revoked_at IS NULL
                    RETURNING {CredentialColumns}
                    """,
                    new { actor = access.Actor.ActorId, reason, credentialId, id });

                if (updated is null)
                {
                    return await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.credentials WHERE id = @credentialId AND person_id = @id", new { credentialId, id }) is null
                        ? Problems.NotFound()
                        : Problems.Conflict("Esta credencial já está revogada.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "credential.revoke", "credential", credentialId.ToString(), AuditWriter.Success, http.GetCorrelationId(), Reason: reason, Metadata: new { person = id }));
                await OutboxWriter.EnqueueAsync(tx, "person.updated.v1", id, http.GetCorrelationId());
                return Results.Ok(updated);
            },
            cancellationToken);
    }

    /// <summary>Quem esta com este cartao. O valor vai no corpo: em URL ficaria em log. So pessoas no alcance de quem pergunta.</summary>
    private static async Task<IResult> LookupAsync(CredentialLookupRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!CredentialKinds.IsSupported(body.Kind) || !CredentialKinds.IsSearchable(body.Kind!))
        {
            return Problems.Invalid("Este tipo de credencial não pode ser buscado.");
        }

        if (!CredentialValue.TryNormalize(body.Kind, body.Value, out var normalized))
        {
            return Problems.Invalid("O valor da credencial é inválido.");
        }

        if (http.RequestServices.GetService<CredentialProtector>() is not { } protector)
        {
            return Unavailable();
        }

        byte[] index;
        try
        {
            index = await protector.IndexAsync(access.Actor.TenantId, body.Kind!, normalized, cancellationToken);
        }
        catch (Exception ex) when (ex is KeyServiceException or HttpRequestException)
        {
            return Unavailable();
        }

        var scope = http.GetScope();
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var person = await tx.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT c.person_id FROM hub.credentials c JOIN hub.people p ON p.tenant_id = c.tenant_id AND p.id = c.person_id
                    WHERE c.kind = @Kind AND c.blind_index = @index AND c.revoked_at IS NULL
                      AND (@all OR p.site_id = ANY(@allowed) OR p.site_id IS NULL) AND (p.site_id IS NULL OR NOT p.site_id = ANY(@denied))
                    """,
                    new { body.Kind, index, all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() });
                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "credential.lookup", "credential", null, AuditWriter.Success, http.GetCorrelationId(), Metadata: new { kind = body.Kind, found = person is not null }));
                return Results.Ok(new CredentialLookupResponse(person is not null, person));
            },
            cancellationToken);
    }

    /// <summary>Nulo quando valido. Visitante sem fim de vigencia nunca expiraria (RF-008).</summary>
    internal static string? Validate(string? name, string? registration, string type, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxName || name.Any(char.IsControl))
        {
            return $"Informe o nome, com até {MaxName} caracteres.";
        }

        if (registration is { Length: 0 or > MaxRegistration } || (registration is not null && registration.Any(char.IsControl)))
        {
            return $"A matrícula deve ter até {MaxRegistration} caracteres.";
        }

        if (!PersonTypes.Contains(type))
        {
            return "Tipo de pessoa inválido.";
        }

        if (from > to)
        {
            return "O início da validade precisa ser antes do fim.";
        }

        return type == "visitor" && to is null ? "Visitante precisa de uma data de fim de validade." : null;
    }

    internal static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private static bool TryVersion(HttpContext http, out int version)
    {
        var header = http.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        return int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static bool InScope(HttpContext http, PersonRow person)
    {
        var scope = http.GetScope();
        return person.SiteId is { } site ? scope.Includes(site) : scope.AllSites;
    }

    private static Task<PersonRow?> FindAsync(TenantTransaction tx, Guid id) =>
        tx.QuerySingleOrDefaultAsync<PersonRow>($"SELECT {Columns} FROM hub.people WHERE id = @id AND deleted_at IS NULL", new { id });

    /// <summary>Quem so le ve a matricula mascarada: o papel decide, nao a tela.</summary>
    private static PersonResponse ToResponse(PersonRow row, PermissionScope writer)
    {
        var full = row.SiteId is { } site ? writer.Includes(site) : writer.AllSites;
        return new PersonResponse(
            row.Id,
            row.SiteId,
            row.DisplayName,
            full ? row.Registration : MaskRegistration(row.Registration),
            !full,
            row.PersonType,
            row.ValidFrom,
            row.ValidTo,
            row.State,
            row.Version,
            row.CreatedAt,
            row.UpdatedAt,
            row.RevokedAt,
            full ? row.RevokeReason : null);
    }

    internal static string? MaskRegistration(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length >= 6 ? string.Concat("****", value.AsSpan(value.Length - 2)) : "****";
    }

    private static DateTime? Utc(DateTimeOffset? value) => value?.UtcDateTime;

    private static DateTimeOffset? Offset(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private static IResult Unavailable() =>
        Problems.Of(StatusCodes.Status503ServiceUnavailable, "key_service_unavailable", "Serviço de chaves indisponível. Nada foi gravado.");

    private sealed record LinkedDevice(string Name, int SourceEpoch, Guid SiteId);
}

internal sealed record PersonRow(
    Guid Id,
    Guid? SiteId,
    string DisplayName,
    string? Registration,
    string PersonType,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    string State,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? RevokedAt,
    string? RevokeReason);
