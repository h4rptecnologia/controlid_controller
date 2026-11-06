using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.IdentityTenancy;

public sealed record SiteResponse(Guid Id, string Name, string Timezone, DateTime CreatedAt);

public sealed record CreateSiteRequest(string? Name, string? Timezone);

public static class SiteEndpoints
{
    private const string Columns = "id AS Id, name AS Name, timezone AS Timezone, created_at AS CreatedAt";

    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/sites").WithTags("Sites");

        group.MapGet("/", ListAsync).Produces<IEnumerable<SiteResponse>>().RequirePermission(Permissions.SitesRead);
        group.MapGet("/{id:guid}", GetAsync).Produces<SiteResponse>().RequirePermission(Permissions.SitesRead);
        group.MapPost("/", CreateAsync).Produces<SiteResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.SitesWrite).RequireIdempotency();

        return routes;
    }

    private static async Task<IResult> ListAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var sites = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<SiteResponse>(
                $"""
                SELECT {Columns} FROM hub.sites
                WHERE (@all OR id = ANY(@allowed)) AND NOT id = ANY(@denied)
                ORDER BY name, id
                """,
                new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() }),
            cancellationToken);

        return Results.Ok(sites);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(id))
        {
            return Problems.NotFound();
        }

        var site = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QuerySingleOrDefaultAsync<SiteResponse>($"SELECT {Columns} FROM hub.sites WHERE id = @id", new { id }),
            cancellationToken);

        return site is null ? Problems.NotFound() : Results.Ok(site);
    }

    private static async Task<IResult> CreateAsync(CreateSiteRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        // Criar unidade nova nao cabe em permissao limitada a unidades existentes.
        if (!http.GetScope().IsTenantWide)
        {
            return Problems.Forbidden();
        }

        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return Problems.Invalid("Informe um nome de unidade com até 200 caracteres.");
        }

        var timezone = string.IsNullOrWhiteSpace(body.Timezone) ? "America/Sao_Paulo" : body.Timezone.Trim();

        var result = await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var known = await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM pg_timezone_names WHERE name = @timezone LIMIT 1", new { timezone });
                if (known is null)
                {
                    return Problems.Invalid("Fuso horário desconhecido. Use um nome IANA, como America/Sao_Paulo.");
                }

                var site = await tx.QuerySingleOrDefaultAsync<SiteResponse>(
                    $"INSERT INTO hub.sites (tenant_id, name, timezone) VALUES (@TenantId, @name, @timezone) RETURNING {Columns}",
                    new { tx.TenantId, name, timezone });

                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, Permissions.SitesWrite, "site", site!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));

                return Results.Created($"/api/v1/sites/{site.Id}", site);
            },
            cancellationToken);

        return result;
    }
}
