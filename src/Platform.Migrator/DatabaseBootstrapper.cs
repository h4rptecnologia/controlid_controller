using Npgsql;

namespace Platform.Migrator;

/// <summary>
/// Cria papeis e banco com uma conta administrativa. Fica fora das migracoes porque papeis sao globais
/// ao cluster e migration_owner nao deve ter CREATEROLE (docs/05-dados.md).
/// </summary>
public static class DatabaseBootstrapper
{
    public const string MigrationOwner = "migration_owner";

    public static readonly IReadOnlyList<string> Roles =
        [MigrationOwner, "app_api", "worker_sync", "worker_import", "report_readonly", "backup"];

    /// <param name="passwords">Papel para senha. Papel ausente permanece NOLOGIN.</param>
    public static async Task RunAsync(
        string adminConnectionString,
        string databaseName,
        IReadOnlyDictionary<string, string> passwords,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var role in Roles)
        {
            if (!await ExistsAsync(connection, "SELECT 1 FROM pg_roles WHERE rolname = @name", role, cancellationToken))
            {
                await ExecuteAsync(connection, $"CREATE ROLE {Quote(role)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS", cancellationToken);
            }

            if (passwords.TryGetValue(role, out var password))
            {
                await SetPasswordAsync(connection, role, password, cancellationToken);
            }
        }

        if (!await ExistsAsync(connection, "SELECT 1 FROM pg_database WHERE datname = @name", databaseName, cancellationToken))
        {
            await ExecuteAsync(connection, $"CREATE DATABASE {Quote(databaseName)} OWNER {Quote(MigrationOwner)}", cancellationToken);
        }

        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {Quote(databaseName)} FROM PUBLIC", cancellationToken);
        foreach (var role in Roles)
        {
            await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {Quote(databaseName)} TO {Quote(role)}", cancellationToken);
        }
    }

    private static async Task SetPasswordAsync(NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // A senha nao pode terminar no log do servidor, mesmo com log_statement=all.
        await using (var silence = new NpgsqlCommand("SET LOCAL log_statement = 'none'", connection, transaction))
        {
            await silence.ExecuteNonQueryAsync(cancellationToken);
        }

        string statement;
        await using (var format = new NpgsqlCommand("SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', @role, @password)", connection, transaction))
        {
            format.Parameters.AddWithValue("role", role);
            format.Parameters.AddWithValue("password", password);
            statement = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        }

#pragma warning disable CA2100 // Literais escapados pelo format() do servidor (%I/%L); ALTER ROLE nao aceita parametros.
        await using (var alter = new NpgsqlCommand(statement, connection, transaction))
#pragma warning restore CA2100
        {
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string sql, string name, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Consultas constantes desta classe.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Identificadores passam por Quote(); DDL nao aceita parametros.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
