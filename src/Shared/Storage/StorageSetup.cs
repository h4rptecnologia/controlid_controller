using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Kernel.Data;

namespace Platform.Shared.Storage;

public static class StorageSetup
{
    /// <summary>
    /// Registra o armazenamento somente com configuracao completa. Sem ela EncryptedBlobStore nao existe e quem
    /// depende dele responde indisponivel; nao ha chave local de reserva (docs/06: "nao em .env ou imagem").
    /// </summary>
    public static IServiceCollection AddHubStorage(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var kms = configuration.GetSection("Kms");
        var storage = configuration.GetSection("Storage");
        if (string.IsNullOrWhiteSpace(kms["Address"]) || string.IsNullOrWhiteSpace(storage["ServiceUrl"]))
        {
            return services;
        }

        var kmsAddress = RequireSecureUri(kms["Address"]!, "Kms:Address", environment);
        var storageUrl = RequireSecureUri(storage["ServiceUrl"]!, "Storage:ServiceUrl", environment);
        var bucket = Required(storage, "Bucket");

        services.AddHttpClient<IKeyService, TransitKeyService>(client =>
        {
            client.BaseAddress = kmsAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-Vault-Token", Required(kms, "Token"));
        });

        services.AddTransient<CredentialProtector>();

        services.AddSingleton(_ => S3ObjectStore.CreateClient(storageUrl.ToString(), Required(storage, "AccessKey"), Required(storage, "SecretKey")));
        services.AddSingleton(provider => new S3ObjectStore(provider.GetRequiredService<Amazon.S3.IAmazonS3>(), bucket));
        services.AddSingleton<IObjectStore>(provider => provider.GetRequiredService<S3ObjectStore>());
        services.AddTransient(provider => new EncryptedBlobStore(
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<IKeyService>(),
            provider.GetRequiredService<ITenantDatabase>()));

        if (string.Equals(storage["CreateBucketIfMissing"], "true", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<BucketInitializer>();
        }

        services.AddHealthChecks().AddCheck<StorageReadinessCheck>("storage", tags: ["ready"]);
        return services;
    }

    // Em producao nada de HTTP puro para chaves ou arquivos, nem por configuracao.
    private static Uri RequireSecureUri(string value, string name, IHostEnvironment environment)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{name} invalido.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && environment.IsProduction())
        {
            throw new InvalidOperationException($"{name} precisa usar HTTPS em producao.");
        }

        return uri;
    }

    private static string Required(IConfiguration section, string name) =>
        section[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuracao ausente: {name}");

    private sealed class BucketInitializer(S3ObjectStore store) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // So em ambiente de desenvolvimento: o S3 pode ainda estar subindo quando a API inicia.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await store.EnsureBucketAsync(cancellationToken);
                    return;
                }
                catch (Exception ex) when (attempt < 30 && ex is Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException or HttpRequestException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StorageReadinessCheck(Amazon.S3.IAmazonS3 client, IConfiguration configuration) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await client.GetBucketLocationAsync(configuration["Storage:Bucket"], cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException or HttpRequestException)
            {
                return HealthCheckResult.Unhealthy("unavailable");
            }
        }
    }
}
