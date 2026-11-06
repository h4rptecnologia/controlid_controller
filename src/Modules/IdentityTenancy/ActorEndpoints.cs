using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.IdentityTenancy;

public sealed record MeResponse(Guid ActorId, string DisplayName, bool Mfa, IReadOnlyList<GrantResponse> Grants);

public sealed record ActorResponse(Guid Id, string DisplayName, string OidcSubject, DateTime? DisabledAt);

public sealed record CreateActorRequest(string? OidcSubject, string? DisplayName);

public sealed record GrantResponse(Guid Id, string Action, Guid? SiteId, string Effect);

public sealed record CreateGrantRequest(string? Action, Guid? SiteId, string? Effect);

public static class ActorEndpoints
{
    private const string ActorColumns = "id AS Id, display_name AS DisplayName, oidc_subject AS OidcSubject, disabled_at AS DisabledAt";
    private const string GrantColumns = "id AS Id, action_name AS Action, site_id AS SiteId, effect AS Effect";

    public static IEndpointRouteBuilder MapActorEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/me", MeAsync).WithTags("Actors").Produces<MeResponse>().RequireActor();

        var group = routes.MapGroup("/api/v1/actors").WithTags("Actors");
        group.MapGet("/", ListAsync).Produces<IEnumerable<ActorResponse>>().RequirePermission(Permissions.ActorsRead);
        group.MapPost("/", CreateAsync).Produces<ActorResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.ActorsManage).RequireIdempotency();
        group.MapPost("/{id:guid}/disable", DisableAsync).Produces(StatusCodes.Status204NoContent).RequireRateLimiting("sensitive").RequirePermission(Permissions.ActorsManage).RequireIdempotency();
        group.MapGet("/{id:guid}/grants", ListGrantsAsync).Produces<IEnumerable<GrantResponse>>().RequirePermission(Permissions.ActorsRead);
        group.MapPost("/{id:guid}/grants", AddGrantAsync).Produces<GrantResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.ActorsManage).RequireIdempotency();
        group.MapDelete("/{id:guid}/grants/{grantId:guid}", RemoveGrantAsync).Produces(StatusCodes.Status204NoContent).RequireRateLimiting("sensitive").RequirePermission(Permissions.ActorsManage).RequireIdempotency();

        return routes;
    }

    private static async Task<IResult> MeAsync(RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var actor = access.Actor;
        var grants = await database.InTenantAsync(
            actor.TenantId,
            tx => tx.QueryAsync<GrantResponse>($"SELECT {GrantColumns} FROM hub.actor_grants WHERE actor_id = @id ORDER BY action_name, id", new { id = actor.ActorId }),
            cancellationToken);

        return Results.Ok(new MeResponse(actor.ActorId, actor.DisplayName, actor.MfaSatisfied, grants.ToList()));
    }

    private static async Task<IResult> ListAsync(RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var actors = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<ActorResponse>($"SELECT {ActorColumns} FROM hub.actors ORDER BY display_name, id"),
            cancellationToken);

        return Results.Ok(actors);
    }

    private static async Task<IResult> CreateAsync(CreateActorRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var subject = body.OidcSubject?.Trim();
        var name = body.DisplayName?.Trim();
        if (string.IsNullOrEmpty(subject) || subject.Length > 255 || string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return Problems.Invalid("Informe o identificador OIDC (até 255 caracteres) e o nome (até 200 caracteres).");
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var created = await tx.QuerySingleOrDefaultAsync<ActorResponse>(
                    $"""
                    INSERT INTO hub.actors (tenant_id, oidc_subject, display_name) VALUES (@TenantId, @subject, @name)
                    ON CONFLICT (tenant_id, oidc_subject) DO NOTHING
                    RETURNING {ActorColumns}
                    """,
                    new { tx.TenantId, subject, name });

                if (created is null)
                {
                    return Problems.Conflict("Já existe um operador com este identificador.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, Permissions.ActorsManage, "actor", created.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));

                return Results.Created($"/api/v1/actors/{created.Id}", created);
            },
            cancellationToken);
    }

    private static async Task<IResult> DisableAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (id == access.Actor.ActorId)
        {
            return Problems.Forbidden("self_change_forbidden", "Você não pode desativar a própria conta.");
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var changed = await tx.ExecuteAsync("UPDATE hub.actors SET disabled_at = now() WHERE id = @id AND disabled_at IS NULL", new { id });
                if (changed == 0)
                {
                    return Problems.NotFound();
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, "actor.disable", "actor", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));

                return Results.NoContent();
            },
            cancellationToken);
    }

    private static async Task<IResult> ListGrantsAsync(Guid id, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (!await ActorExistsAsync(tx, id))
                {
                    return Problems.NotFound();
                }

                return Results.Ok(await tx.QueryAsync<GrantResponse>(
                    $"SELECT {GrantColumns} FROM hub.actor_grants WHERE actor_id = @id ORDER BY action_name, id", new { id }));
            },
            cancellationToken);

    private static async Task<IResult> AddGrantAsync(Guid id, CreateGrantRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var effect = body.Effect ?? PermissionScope.Allow;
        if (body.Action is null || !Permissions.Catalog.TryGetValue(body.Action, out var permission)
            || effect is not (PermissionScope.Allow or PermissionScope.Deny))
        {
            return Problems.Invalid("Permissão ou efeito inválido.");
        }

        if (id == access.Actor.ActorId)
        {
            return Problems.Forbidden("self_change_forbidden", "Você não pode alterar as próprias permissões.");
        }

        // Negar so reduz acesso; conceder nao pode ir alem do que o proprio ator tem.
        if (effect == PermissionScope.Allow)
        {
            var denial = await CheckCanGrantAsync(permission, body.SiteId, access, cancellationToken);
            if (denial is not null)
            {
                return denial;
            }
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (!await ActorExistsAsync(tx, id))
                {
                    return Problems.NotFound();
                }

                if (body.SiteId is { } siteId
                    && await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @siteId", new { siteId }) is null)
                {
                    return Problems.NotFound();
                }

                var grant = await tx.QuerySingleOrDefaultAsync<GrantResponse>(
                    $"""
                    INSERT INTO hub.actor_grants (tenant_id, actor_id, site_id, action_name, effect, granted_by)
                    VALUES (@TenantId, @id, @SiteId, @Action, @effect, @grantedBy)
                    ON CONFLICT DO NOTHING
                    RETURNING {GrantColumns}
                    """,
                    new { tx.TenantId, id, body.SiteId, body.Action, effect, grantedBy = access.Actor.ActorId });

                if (grant is null)
                {
                    return Problems.Conflict("Esta permissão já está registrada.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, "grant.add", "actor", id.ToString(), AuditWriter.Success, http.GetCorrelationId(),
                    Metadata: new { permission = body.Action, effect, siteId = body.SiteId }));

                return Results.Created($"/api/v1/actors/{id}/grants/{grant.Id}", grant);
            },
            cancellationToken);
    }

    private static async Task<IResult?> CheckCanGrantAsync(PermissionInfo permission, Guid? siteId, RequestAccess access, CancellationToken cancellationToken)
    {
        if (permission.Sensitive)
        {
            var sensitive = await access.GetScopeAsync(Permissions.GrantsSensitive, cancellationToken);
            return sensitive.IsTenantWide
                ? null
                : Problems.Forbidden("sensitive_grant_forbidden", "Conceder esta permissão exige autorização específica.");
        }

        var held = await access.GetScopeAsync(permission.Name, cancellationToken);
        var covered = siteId is { } site ? held.Includes(site) : held.IsTenantWide;
        return covered ? null : Problems.Forbidden("grant_exceeds_own_access", "Você não pode conceder um acesso que não possui.");
    }

    private static async Task<IResult> RemoveGrantAsync(Guid id, Guid grantId, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (id == access.Actor.ActorId)
        {
            return Problems.Forbidden("self_change_forbidden", "Você não pode alterar as próprias permissões.");
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var removed = await tx.QuerySingleOrDefaultAsync<GrantResponse>(
                    $"DELETE FROM hub.actor_grants WHERE id = @grantId AND actor_id = @id RETURNING {GrantColumns}",
                    new { id, grantId });

                if (removed is null)
                {
                    return Problems.NotFound();
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, "grant.remove", "actor", id.ToString(), AuditWriter.Success, http.GetCorrelationId(),
                    Metadata: new { permission = removed.Action, effect = removed.Effect, siteId = removed.SiteId }));

                return Results.NoContent();
            },
            cancellationToken);
    }

    private static async Task<bool> ActorExistsAsync(TenantTransaction tx, Guid id) =>
        await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.actors WHERE id = @id", new { id }) is not null;
}
