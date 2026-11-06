using Npgsql;
using Platform.Migrator;
using Testcontainers.PostgreSql;

namespace Platform.IntegrationTests;

/// <summary>PostgreSQL real, provisionado pelo mesmo bootstrap e migracoes usados no deploy.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:16-alpine@sha256:aa6cb33cf4c19c765cefcf48d96176258800870a9a1e930ad126200f9d825139";
    private const string Database = "hub";

    private readonly string appPassword = Guid.NewGuid().ToString("N");
    private readonly string ownerPassword = Guid.NewGuid().ToString("N");
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(Image).Build();

    public string AdminConnection => container.GetConnectionString();

    public string AppConnection => For("app_api", appPassword);

    public string OwnerConnection => For(DatabaseBootstrapper.MigrationOwner, ownerPassword);

    public string AdminOnHubConnection => new NpgsqlConnectionStringBuilder(AdminConnection) { Database = Database }.ConnectionString;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await DatabaseBootstrapper.RunAsync(AdminConnection, Database, new Dictionary<string, string>
        {
            [DatabaseBootstrapper.MigrationOwner] = ownerPassword,
            ["app_api"] = appPassword,
        });

        var result = await MigrationRunner.RunAsync(OwnerConnection);
        if (!result.Successful)
        {
            throw new InvalidOperationException($"Migracao falhou: {result.ErrorScript?.Name}", result.Error);
        }
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private string For(string user, string password) =>
        new NpgsqlConnectionStringBuilder(AdminConnection) { Database = Database, Username = user, Password = password }.ConnectionString;
}

[CollectionDefinition(Name)]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>, ICollectionFixture<StorageFixture>
{
    public const string Name = "postgres";
}
