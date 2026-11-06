using Platform.Migrator;

const string Usage = "Uso: Platform.Migrator <bootstrap|migrate|provision-tenant <slug> <nome> <oidc-subject> <nome-admin>>";

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

switch (args[0])
{
    case "bootstrap" when args.Length == 1:
        {
            var passwords = DatabaseBootstrapper.Roles
                .Select(role => (role, value: Environment.GetEnvironmentVariable($"HUB_PASSWORD_{role.ToUpperInvariant()}")))
                .Where(item => !string.IsNullOrEmpty(item.value))
                .ToDictionary(item => item.role, item => item.value!);

            await DatabaseBootstrapper.RunAsync(
                Required("HUB_ADMIN_CONNECTION"),
                Environment.GetEnvironmentVariable("HUB_DATABASE") ?? "hub",
                passwords);
            Console.WriteLine($"Bootstrap concluido: {DatabaseBootstrapper.Roles.Count} papeis, {passwords.Count} com login.");
            return 0;
        }

    case "migrate" when args.Length == 1:
        {
            var result = await MigrationRunner.RunAsync(Required("HUB_MIGRATOR_CONNECTION"));
            if (!result.Successful)
            {
                Console.Error.WriteLine($"Migracao falhou em {result.ErrorScript?.Name ?? "?"}.");
                return 1;
            }

            Console.WriteLine($"Migracoes aplicadas: {result.Scripts.Count()}.");
            return 0;
        }

    case "provision-tenant" when args.Length == 5:
        {
            var tenantId = await TenantProvisioner.RunAsync(Required("HUB_ADMIN_HUB_CONNECTION"), args[1], args[2], args[3], args[4]);
            Console.WriteLine($"Tenant criado: {tenantId}.");
            return 0;
        }

    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Variavel de ambiente obrigatoria ausente: {name}");
