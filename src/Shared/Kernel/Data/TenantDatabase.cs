using Dapper;
using Npgsql;

namespace Platform.Shared.Kernel.Data;

/// <summary>Transacao com o contexto de RLS ja definido. E a unica forma de chegar a dados de tenant.</summary>
public sealed class TenantTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId)
{
    public Guid TenantId { get; } = tenantId;

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null) =>
        connection.QueryAsync<T>(sql, parameters, transaction);

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null) =>
        connection.QuerySingleOrDefaultAsync<T>(sql, parameters, transaction);

    public Task<int> ExecuteAsync(string sql, object? parameters = null) =>
        connection.ExecuteAsync(sql, parameters, transaction);
}

public interface ITenantDatabase
{
    Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantTransaction, Task<T>> work, CancellationToken cancellationToken);

    Task<Guid?> ResolveTenantIdAsync(string slug, CancellationToken cancellationToken);
}

public sealed class TenantDatabase(NpgsqlDataSource dataSource) : ITenantDatabase
{
    public async Task<T> InTenantAsync<T>(Guid tenantId, Func<TenantTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // set_config(..., true) vale so nesta transacao: a conexao volta ao pool sem contexto.
        await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tenant, true)", new { tenant = tenantId.ToString() }, transaction);

        var result = await work(new TenantTransaction(connection, transaction, tenantId));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<Guid?> ResolveTenantIdAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<Guid?>("SELECT hub.resolve_tenant_id(@slug)", new { slug });
    }
}
