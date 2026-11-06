using Npgsql;
using Platform.Migrator;

namespace Platform.IntegrationTests;

[Collection(PostgresTests.Name)]
public sealed class TenantIsolationTests(PostgresFixture fixture)
{
    private async Task<(Guid TenantA, Guid TenantB)> SeedAsync()
    {
        // Semeia como superusuario: hub.tenants nao tem grant para o app e o FORCE RLS barra o owner sem contexto.
        await using var admin = new NpgsqlConnection(fixture.AdminOnHubConnection);
        await admin.OpenAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO hub.tenants (id, slug, name) VALUES (@a, @sa, 'A'), (@b, @sb, 'B');
            INSERT INTO hub.sites (tenant_id, name) VALUES (@a, 'site-a'), (@b, 'site-b');
            """, admin);
        command.Parameters.AddWithValue("a", a);
        command.Parameters.AddWithValue("b", b);
        command.Parameters.AddWithValue("sa", "t-" + a.ToString("N"));
        command.Parameters.AddWithValue("sb", "t-" + b.ToString("N"));
        await command.ExecuteNonQueryAsync();
        return (a, b);
    }

    private async Task<NpgsqlConnection> OpenAppAsync()
    {
        var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction);
        command.Parameters.AddWithValue("t", tenant.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountSitesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenant)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM hub.sites WHERE tenant_id = @t", connection, transaction);
        command.Parameters.AddWithValue("t", tenant);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task App_role_cannot_bypass_rls()
    {
        await using var connection = await OpenAppAsync();
        await using var command = new NpgsqlCommand("SELECT rolsuper OR rolbypassrls OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = current_user", connection);
        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Every_hub_table_except_tenants_has_forced_rls()
    {
        await using var connection = new NpgsqlConnection(fixture.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'hub' AND c.relkind = 'r' AND c.relname <> 'tenants'
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var unprotected = new List<string>();
        while (await reader.ReadAsync())
        {
            unprotected.Add(reader.GetString(0));
        }

        Assert.Empty(unprotected);
    }

    [Fact]
    public async Task Without_tenant_context_reads_nothing_and_writes_fail()
    {
        var (a, _) = await SeedAsync();
        await using var connection = await OpenAppAsync();

        Assert.Equal(0, await CountSitesAsync(connection, null, a));

        await using var insert = new NpgsqlCommand("INSERT INTO hub.sites (tenant_id, name) VALUES (@t, 'x')", connection);
        insert.Parameters.AddWithValue("t", a);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Tenant_a_cannot_read_or_change_tenant_b()
    {
        var (a, b) = await SeedAsync();
        await using var connection = await OpenAppAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, a);

        Assert.Equal(1, await CountSitesAsync(connection, transaction, a));
        Assert.Equal(0, await CountSitesAsync(connection, transaction, b));

        await using var update = new NpgsqlCommand("UPDATE hub.sites SET name = 'hack' WHERE tenant_id = @b", connection, transaction);
        update.Parameters.AddWithValue("b", b);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());

        await using var delete = new NpgsqlCommand("DELETE FROM hub.sites WHERE tenant_id = @b", connection, transaction);
        delete.Parameters.AddWithValue("b", b);
        Assert.Equal(0, await delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Tenant_a_cannot_insert_or_move_rows_into_tenant_b()
    {
        var (a, b) = await SeedAsync();
        await using var connection = await OpenAppAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, transaction, a);
            await using var insert = new NpgsqlCommand("INSERT INTO hub.sites (tenant_id, name) VALUES (@b, 'x')", connection, transaction);
            insert.Parameters.AddWithValue("b", b);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, transaction, a);
            await using var move = new NpgsqlCommand("UPDATE hub.sites SET tenant_id = @b WHERE tenant_id = @a", connection, transaction);
            move.Parameters.AddWithValue("a", a);
            move.Parameters.AddWithValue("b", b);
            var error = await Assert.ThrowsAsync<PostgresException>(() => move.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task Tenant_context_does_not_leak_to_next_transaction_on_same_connection()
    {
        var (a, _) = await SeedAsync();
        await using var connection = await OpenAppAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, transaction, a);
            Assert.Equal(1, await CountSitesAsync(connection, transaction, a));
            await transaction.CommitAsync();
        }

        Assert.Equal(0, await CountSitesAsync(connection, null, a));
    }

    [Fact]
    public async Task App_cannot_enumerate_tenants()
    {
        await using var connection = await OpenAppAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM hub.tenants", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Theory]
    [InlineData("UPDATE hub.audit_log SET outcome = 'x'")]
    [InlineData("DELETE FROM hub.audit_log")]
    [InlineData("UPDATE hub.access_events SET event_kind = 'x'")]
    [InlineData("DELETE FROM hub.access_events")]
    public async Task Append_only_tables_reject_changes_from_app(string sql)
    {
        await using var connection = await OpenAppAsync();
#pragma warning disable CA2100 // Constantes do proprio teste.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        var result = await MigrationRunner.RunAsync(fixture.OwnerConnection);
        Assert.True(result.Successful);
        Assert.Empty(result.Scripts);
    }
}
