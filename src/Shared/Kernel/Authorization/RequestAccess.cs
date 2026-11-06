using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Shared.Kernel.Authorization;

public sealed record ActorContext(Guid TenantId, Guid ActorId, string DisplayName, bool MfaSatisfied, DateTimeOffset? AuthenticatedAt);

/// <summary>Marca a rota como autorizada; um teste exige isto em toda rota de /api.</summary>
public sealed record PermissionMetadata(string? Permission);

/// <summary>Identidade da requisicao. Tenant e ator vem so do principal autenticado, nunca do payload.</summary>
public sealed class RequestAccess(IHttpContextAccessor accessor, ITenantDatabase database, IConfiguration configuration)
{
    // Valores de "amr" que indicam segundo fator (RFC 8176). A homologar com o provedor OIDC escolhido.
    private static readonly HashSet<string> MfaMethods = new(StringComparer.Ordinal) { "mfa", "otp", "hwk", "swk", "fido", "sms" };

    private ActorContext? actor;
    private bool resolved;

    public ActorContext Actor => actor ?? throw new InvalidOperationException("Rota sem filtro de autorizacao.");

    public async Task<ActorContext?> ResolveActorAsync(CancellationToken cancellationToken)
    {
        if (resolved)
        {
            return actor;
        }

        resolved = true;
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = user.FindFirstValue("sub");
        var slug = user.FindFirstValue(configuration["Oidc:TenantClaim"] ?? "tenant");
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var tenantId = await database.ResolveTenantIdAsync(slug, cancellationToken);
        if (tenantId is not { } tenant)
        {
            return null;
        }

        var row = await database.InTenantAsync(
            tenant,
            tx => tx.QuerySingleOrDefaultAsync<ActorRow>(
                "SELECT id AS Id, display_name AS DisplayName FROM hub.actors WHERE oidc_subject = @subject AND disabled_at IS NULL",
                new { subject }),
            cancellationToken);

        if (row is null)
        {
            return null;
        }

        var mfa = user.FindAll("amr").Any(c => MfaMethods.Contains(c.Value));
        var authTime = long.TryParse(user.FindFirstValue("auth_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;

        actor = new ActorContext(tenant, row.Id, row.DisplayName, mfa, authTime);
        return actor;
    }

    public Task<PermissionScope> GetScopeAsync(string permission, CancellationToken cancellationToken) =>
        database.InTenantAsync(
            Actor.TenantId,
            async tx => PermissionScope.Evaluate(await tx.QueryAsync<GrantRow>(
                "SELECT site_id AS SiteId, effect AS Effect FROM hub.actor_grants WHERE actor_id = @actorId AND action_name = @permission",
                new { actorId = Actor.ActorId, permission })),
            cancellationToken);

    private sealed record ActorRow(Guid Id, string DisplayName);
}

public static class AuthorizationEndpointExtensions
{
    /// <summary>Exige ator valido e a permissao exata. O alcance por unidade fica em HttpContext.GetScope().</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        if (!Permissions.Catalog.TryGetValue(permission, out var info))
        {
            throw new ArgumentException($"Permissao fora do catalogo: {permission}", nameof(permission));
        }

        return builder.WithMetadata(new PermissionMetadata(permission))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var access = http.RequestServices.GetRequiredService<RequestAccess>();
            if (await access.ResolveActorAsync(http.RequestAborted) is not { } actor)
            {
                return Problems.Forbidden("no_active_actor", "Sua conta não está habilitada nesta organização.");
            }

            var scope = await access.GetScopeAsync(permission, http.RequestAborted);
            if (scope.IsEmpty)
            {
                await AuditDeniedAsync(http, actor, permission, "no_grant");
                return Problems.Forbidden();
            }

            if (info.RequiresMfa && !actor.MfaSatisfied)
            {
                await AuditDeniedAsync(http, actor, permission, "mfa_required");
                return Problems.Forbidden("mfa_required", "Esta ação exige login com segundo fator.");
            }

            http.Items[ScopeItem] = scope;
            return await next(context);
        });
    }

    /// <summary>Exige apenas ator valido, para rotas sobre o proprio ator.</summary>
    public static RouteHandlerBuilder RequireActor(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new PermissionMetadata(null))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var access = http.RequestServices.GetRequiredService<RequestAccess>();
            return await access.ResolveActorAsync(http.RequestAborted) is null
                ? Problems.Forbidden("no_active_actor", "Sua conta não está habilitada nesta organização.")
                : await next(context);
        });

    public static PermissionScope GetScope(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[ScopeItem] as PermissionScope ?? PermissionScope.None;
    }

    private const string ScopeItem = "hub.scope";

    private static async Task AuditDeniedAsync(HttpContext http, ActorContext actor, string permission, string cause)
    {
        var database = http.RequestServices.GetRequiredService<ITenantDatabase>();
        await database.InTenantAsync(
            actor.TenantId,
            async tx =>
            {
                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    actor.ActorId, permission, "authorization", null, AuditWriter.Denied, http.GetCorrelationId(), Metadata: new { cause }));
                return true;
            },
            http.RequestAborted);
    }
}
