using System.Reflection;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace Platform.Migrator;

public static class MigrationRunner
{
    private const string JournalSchema = "hub_migrations";

    public static async Task<DatabaseUpgradeResult> RunAsync(string migratorConnectionString, CancellationToken cancellationToken = default)
    {
        // O journal fica fora de "hub": la toda tabela recebe RLS por tenant_id.
        await using (var connection = new NpgsqlConnection(migratorConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {JournalSchema}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return DeployChanges.To
            .PostgresqlDatabase(migratorConnectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.StartsWith("migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .JournalToPostgresqlTable(JournalSchema, "schemaversions")
            .LogToConsole()
            .Build()
            .PerformUpgrade();
    }
}
