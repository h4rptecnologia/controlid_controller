using Npgsql;
using Platform.Shared.Kernel.Authorization;

namespace Platform.Migrator;

/// <summary>
/// Cria um tenant e seu primeiro administrador. Roda com conta administrativa porque o app nao enxerga
/// hub.tenants. O administrador recebe grants.sensitive, mas nao door.unlock, biometrics.read nem keys.rotate:
/// como ninguem altera as proprias permissoes, essas exigem uma segunda pessoa.
/// </summary>
public static class TenantProvisioner
{
    public static async Task<Guid> RunAsync(
        string adminHubConnectionString,
        string slug,
        string tenantName,
        string adminSubject,
        string adminName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(adminHubConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var insert = new NpgsqlCommand(
            """
            WITH t AS (INSERT INTO hub.tenants (slug, name) VALUES (@slug, @name) RETURNING id),
                 a AS (INSERT INTO hub.actors (tenant_id, oidc_subject, display_name)
                       SELECT id, @subject, @admin FROM t RETURNING id, tenant_id),
                 g AS (INSERT INTO hub.actor_grants (tenant_id, actor_id, action_name)
                       SELECT a.tenant_id, a.id, p FROM a, unnest(@permissions) AS p RETURNING 1),
                 l AS (INSERT INTO hub.audit_log (tenant_id, actor_id, action, resource_type, resource_id, outcome)
                       SELECT a.tenant_id, a.id, 'tenant.provision', 'tenant', a.tenant_id::text, 'success' FROM a RETURNING 1)
            SELECT tenant_id FROM a, (SELECT count(*) FROM g) gc, (SELECT count(*) FROM l) lc
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("slug", slug);
        insert.Parameters.AddWithValue("name", tenantName);
        insert.Parameters.AddWithValue("subject", adminSubject);
        insert.Parameters.AddWithValue("admin", adminName);
        insert.Parameters.AddWithValue("permissions", InitialPermissions());

        var tenantId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return tenantId;
    }

    public static string[] InitialPermissions() =>
        Permissions.Catalog.Values
            .Where(p => !p.Sensitive || p.Name == Permissions.GrantsSensitive)
            .Select(p => p.Name)
            .ToArray();
}
