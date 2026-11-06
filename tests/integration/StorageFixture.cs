using System.Net.Http.Json;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests;

/// <summary>
/// OpenBao em modo dev (mesma API de transit do Vault) e um servidor S3 local. O segredo de teste morre com o
/// container. Isto prova o codigo contra servidores reais, nao a configuracao de producao deles.
/// </summary>
public sealed class StorageFixture : IAsyncLifetime
{
    public const string Bucket = "hub-test";
    private const string BaoImage = "openbao/openbao:2@sha256:5046911075e137e154ce49ab051502f4ec7cf367d484ae4a4dce665d9b90561f";
    private const string S3Image = "rustfs/rustfs:latest@sha256:ed9210d6411d77ffceb70d94b964a3cb7d225983368bc25bebe01e995293dcc2";

    private readonly string token = Guid.NewGuid().ToString("N");

    private readonly IContainer bao;
    private readonly IContainer s3;

    public StorageFixture()
    {
        bao = new ContainerBuilder(BaoImage)
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", token)
            .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
            .Build();

        s3 = new ContainerBuilder(S3Image)
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();
    }

    public string AccessKey { get; } = "test" + Guid.NewGuid().ToString("N")[..12];

    public string SecretKey { get; } = Guid.NewGuid().ToString("N");

    public string Token => token;

    public Uri KmsAddress => new($"http://{bao.Hostname}:{bao.GetMappedPublicPort(8200)}/");

    public Uri S3Address => new($"http://{s3.Hostname}:{s3.GetMappedPublicPort(9000)}/");

#pragma warning disable CA1024 // Cria um novo cliente a cada chamada.
    public IAmazonS3 CreateS3Client() => S3ObjectStore.CreateClient(S3Address.ToString(), AccessKey, SecretKey);
#pragma warning restore CA1024

    public HttpClient CreateKmsClient()
    {
        var client = new HttpClient { BaseAddress = KmsAddress };
        client.DefaultRequestHeaders.Add("X-Vault-Token", token);
        return client;
    }

    public async Task InitializeAsync()
    {
        await bao.StartAsync();
        await s3.StartAsync();

        using var kms = CreateKmsClient();
        var mounted = await kms.PostAsJsonAsync(new Uri("v1/sys/mounts/transit", UriKind.Relative), new { type = "transit" });
        mounted.EnsureSuccessStatusCode();

        using var client = CreateS3Client();
        var store = new S3ObjectStore(client, Bucket);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.EnsureBucketAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < 30 && ex is AmazonS3Exception or HttpRequestException or Amazon.Runtime.AmazonServiceException)
            {
                await Task.Delay(500);
            }
        }
    }

    public async Task DisposeAsync()
    {
        await bao.DisposeAsync();
        await s3.DisposeAsync();
    }
}
