using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests;

[Collection(PostgresTests.Name)]
public sealed class EncryptedBlobStoreTests(PostgresFixture postgres, StorageFixture storage) : IDisposable
{
    private readonly TestData data = new(postgres.AdminOnHubConnection);
    private readonly NpgsqlDataSource dataSource = NpgsqlDataSource.Create(postgres.AppConnection);
    private readonly HttpClient kms = storage.CreateKmsClient();
    private readonly Amazon.S3.IAmazonS3 s3 = storage.CreateS3Client();

    public void Dispose()
    {
        kms.Dispose();
        s3.Dispose();
        dataSource.Dispose();
    }

    private EncryptedBlobStore Store(IKeyService? keys = null, IObjectStore? objects = null) => new(
        objects ?? new S3ObjectStore(s3, StorageFixture.Bucket),
        keys ?? new TransitKeyService(kms),
        new TenantDatabase(dataSource));

    private static Task<BlobReference> PutAsync(EncryptedBlobStore store, Guid tenant, byte[] content, BlobKind kind = BlobKind.ImportOriginal) =>
        store.StoreAsync(tenant, kind, BlobClassification.Personal, new MemoryStream(content), null, null, CancellationToken.None);

    private static async Task<byte[]> ReadAsync(EncryptedBlobStore store, Guid tenant, Guid blob)
    {
        using var output = new MemoryStream();
        Assert.True(await store.ReadAsync(tenant, blob, output, CancellationToken.None));
        return output.ToArray();
    }

    private Task<string> ObjectKeyAsync(Guid blob) =>
        data.ScalarAsync<string>("SELECT object_key FROM hub.blob_objects WHERE id = @blob", new { blob });

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(3 * 1024 * 1024 + 17)]
    [InlineData(9 * 1024 * 1024)]
    public async Task Round_trip_with_real_kms_and_object_storage(int size)
    {
        var tenant = await data.TenantAsync();
        var content = RandomNumberGenerator.GetBytes(size);
        var store = Store();

        var reference = await PutAsync(store, tenant.Id, content);

        Assert.Equal(SHA256.HashData(content), reference.PlaintextSha256);
        Assert.Equal(size, reference.PlaintextSize);
        Assert.Equal(content, await ReadAsync(store, tenant.Id, reference.Id));
    }

    [Fact]
    public async Task Stored_object_and_database_row_hold_no_plaintext_or_raw_key()
    {
        var tenant = await data.TenantAsync();
        var secret = Encoding.UTF8.GetBytes("MARCADOR-SIGILOSO-" + Guid.NewGuid());
        var reference = await PutAsync(Store(), tenant.Id, secret);
        var key = await ObjectKeyAsync(reference.Id);

        using var response = await s3.GetObjectAsync(StorageFixture.Bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        Assert.Equal(-1, buffer.ToArray().AsSpan().IndexOf(secret.AsSpan(0, 18)));

        var wrapped = await data.ScalarAsync<string>("SELECT wrapped_dek FROM hub.blob_objects WHERE id = @Id", new { reference.Id });
        Assert.StartsWith("vault:v", wrapped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Object_tampered_in_storage_is_rejected()
    {
        var tenant = await data.TenantAsync();
        var store = Store();
        var reference = await PutAsync(store, tenant.Id, RandomNumberGenerator.GetBytes(4096));
        var key = await ObjectKeyAsync(reference.Id);

        using var response = await s3.GetObjectAsync(StorageFixture.Bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        var tampered = buffer.ToArray();
        tampered[^1] ^= 0x01;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = StorageFixture.Bucket, Key = key, InputStream = new MemoryStream(tampered) });

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<BlobIntegrityException>(() => store.ReadAsync(tenant.Id, reference.Id, output, CancellationToken.None));
    }

    [Fact]
    public async Task Other_tenant_cannot_see_the_blob()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var store = Store();
        var reference = await PutAsync(store, a.Id, [1, 2, 3]);

        using var output = new MemoryStream();
        Assert.False(await store.ReadAsync(b.Id, reference.Id, output, CancellationToken.None));
        Assert.Equal(0, output.Length);
        Assert.False(await store.DestroyAsync(b.Id, reference.Id, CancellationToken.None));
        Assert.Equal([1, 2, 3], await ReadAsync(store, a.Id, reference.Id));
    }

    [Fact]
    public async Task Row_copied_to_other_tenant_still_cannot_be_decrypted()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var store = Store();
        var reference = await PutAsync(store, a.Id, [9, 9, 9]);

        // Simula um ataque direto ao banco: a linha vai para outro tenant, mantendo DEK e objeto.
        await data.ExecuteAsync("UPDATE hub.blob_objects SET tenant_id = @target WHERE id = @Id", new { target = b.Id, reference.Id });

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<BlobIntegrityException>(() => store.ReadAsync(b.Id, reference.Id, output, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task Wrapped_key_from_one_tenant_is_useless_under_another_tenant_key()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var service = new TransitKeyService(kms);
        var wrapped = await service.WrapAsync(EncryptedBlobStore.KeyName(a.Id, KeyPurpose.General), new byte[32], CancellationToken.None);
        await service.WrapAsync(EncryptedBlobStore.KeyName(b.Id, KeyPurpose.General), new byte[32], CancellationToken.None);

        await Assert.ThrowsAsync<KeyServiceException>(() => service.UnwrapAsync(EncryptedBlobStore.KeyName(b.Id, KeyPurpose.General), wrapped, CancellationToken.None));
    }

    [Fact]
    public async Task Biometric_blob_uses_segregated_key()
    {
        var tenant = await data.TenantAsync();
        var reference = await PutAsync(Store(), tenant.Id, [1], BlobKind.BiometricTemplate);

        var key = await data.ScalarAsync<string>("SELECT key_name FROM hub.blob_objects WHERE id = @Id", new { reference.Id });

        Assert.Equal(EncryptedBlobStore.KeyName(tenant.Id, KeyPurpose.Biometric), key);
    }

    [Fact]
    public async Task Rotation_rewraps_without_rewriting_objects_and_old_version_can_be_retired()
    {
        var tenant = await data.TenantAsync();
        var store = Store();
        var content = RandomNumberGenerator.GetBytes(2048);
        var first = await PutAsync(store, tenant.Id, content);
        var second = await PutAsync(store, tenant.Id, content);
        var keyName = EncryptedBlobStore.KeyName(tenant.Id, KeyPurpose.General);
        var etagBefore = (await s3.GetObjectMetadataAsync(StorageFixture.Bucket, await ObjectKeyAsync(first.Id))).ETag;

        await store.RotateKeyAsync(tenant.Id, KeyPurpose.General, CancellationToken.None);

        var partial = await store.RewrapBatchAsync(tenant.Id, KeyPurpose.General, 1, CancellationToken.None);
        Assert.Equal(1, partial.Rewrapped);
        Assert.Equal(1, partial.Remaining);

        var done = await store.RewrapBatchAsync(tenant.Id, KeyPurpose.General, 10, CancellationToken.None);
        Assert.Equal(1, done.Rewrapped);
        Assert.Equal(0, done.Remaining);

        Assert.StartsWith("vault:v2:", await data.ScalarAsync<string>("SELECT wrapped_dek FROM hub.blob_objects WHERE id = @Id", new { first.Id }), StringComparison.Ordinal);
        Assert.Equal(etagBefore, (await s3.GetObjectMetadataAsync(StorageFixture.Bucket, await ObjectKeyAsync(first.Id))).ETag);

        // Aposenta a v1: tudo continua legivel porque ja foi re-embrulhado.
        var retire = await kms.PostAsJsonAsync(new Uri($"v1/transit/keys/{keyName}/config", UriKind.Relative), new { min_decryption_version = 2 });
        retire.EnsureSuccessStatusCode();

        Assert.Equal(content, await ReadAsync(store, tenant.Id, first.Id));
        Assert.Equal(content, await ReadAsync(store, tenant.Id, second.Id));
    }

    [Fact]
    public async Task Rotating_a_key_never_used_before_works()
    {
        var tenant = await data.TenantAsync();

        await Store().RotateKeyAsync(tenant.Id, KeyPurpose.Secret, CancellationToken.None);

        var wrapped = await new TransitKeyService(kms).WrapAsync(EncryptedBlobStore.KeyName(tenant.Id, KeyPurpose.Secret), new byte[32], CancellationToken.None);
        Assert.StartsWith("vault:v2:", wrapped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Destroy_removes_the_key_and_keeps_evidence()
    {
        var tenant = await data.TenantAsync();
        var store = Store();
        var reference = await PutAsync(store, tenant.Id, [5, 5, 5]);
        var key = await ObjectKeyAsync(reference.Id);

        Assert.True(await store.DestroyAsync(tenant.Id, reference.Id, CancellationToken.None));

        using var output = new MemoryStream();
        Assert.False(await store.ReadAsync(tenant.Id, reference.Id, output, CancellationToken.None));
        Assert.Equal(1L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.blob_objects WHERE id = @Id AND wrapped_dek IS NULL AND destroyed_at IS NOT NULL", new { reference.Id }));
        await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() => s3.GetObjectMetadataAsync(StorageFixture.Bucket, key));
        Assert.False(await store.DestroyAsync(tenant.Id, reference.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_read_of_the_bucket_is_refused()
    {
        var tenant = await data.TenantAsync();
        var reference = await PutAsync(Store(), tenant.Id, [1, 2, 3]);
        var key = await ObjectKeyAsync(reference.Id);

        using var anonymous = new HttpClient();
        var objectResponse = await anonymous.GetAsync(new Uri(storage.S3Address, $"{StorageFixture.Bucket}/{key}"));
        var listResponse = await anonymous.GetAsync(new Uri(storage.S3Address, StorageFixture.Bucket));

        Assert.Equal(HttpStatusCode.Forbidden, objectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
    }

    [Fact]
    public async Task Key_service_failure_stores_nothing_and_does_not_leak_details()
    {
        var tenant = await data.TenantAsync();
        using var unauthorized = new HttpClient { BaseAddress = storage.KmsAddress };
        unauthorized.DefaultRequestHeaders.Add("X-Vault-Token", "token-invalido-marcador");

        var error = await Assert.ThrowsAsync<KeyServiceException>(() => PutAsync(Store(new TransitKeyService(unauthorized)), tenant.Id, [1]));

        Assert.DoesNotContain("marcador", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.blob_objects WHERE tenant_id = @Id", new { tenant.Id }));
    }

    [Fact]
    public async Task App_role_cannot_delete_blob_evidence()
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("DELETE FROM hub.blob_objects", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}

[Collection(PostgresTests.Name)]
public sealed class KeyEndpointTests(PostgresFixture postgres, StorageFixture storage) : IDisposable
{
    private readonly TestData data = new(postgres.AdminOnHubConnection);
    private readonly HubApiFactory factory = new(postgres.AppConnection, new Dictionary<string, string?>
    {
        ["Kms:Address"] = storage.KmsAddress.ToString(),
        ["Kms:Token"] = storage.Token,
        ["Storage:ServiceUrl"] = storage.S3Address.ToString(),
        ["Storage:Bucket"] = StorageFixture.Bucket,
        ["Storage:AccessKey"] = storage.AccessKey,
        ["Storage:SecretKey"] = storage.SecretKey,
    });

    public void Dispose() => factory.Dispose();

    private static Uri Url(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task Rotation_requires_specific_permission_and_mfa()
    {
        var tenant = await data.TenantAsync();
        var admin = await data.ActorAsync(tenant, Permissions.ActorsManage, Permissions.GrantsSensitive);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(admin, mfa: true).PostAsync(Url("/api/v1/keys/general/rotate"), null)).StatusCode);

        var holder = await data.ActorAsync(tenant, Permissions.KeysRotate);
        var withoutMfa = await factory.ClientFor(holder).PostAsync(Url("/api/v1/keys/general/rotate"), null);
        Assert.Equal(HttpStatusCode.Forbidden, withoutMfa.StatusCode);
        Assert.Equal("mfa_required", await withoutMfa.ProblemCodeAsync());
    }

    [Fact]
    public async Task Rotation_and_rewrap_are_audited_and_stay_in_own_tenant()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var holder = await data.ActorAsync(a, Permissions.KeysRotate);
        var client = factory.ClientFor(holder, mfa: true);

        // A chave do tenant so existe depois do primeiro uso; o outro tenant nao e tocado.
        using var kms = storage.CreateKmsClient();
        var service = new TransitKeyService(kms);
        var wrappedA = await service.WrapAsync(EncryptedBlobStore.KeyName(a.Id, KeyPurpose.General), new byte[32], CancellationToken.None);
        var wrappedB = await service.WrapAsync(EncryptedBlobStore.KeyName(b.Id, KeyPurpose.General), new byte[32], CancellationToken.None);
        Assert.StartsWith("vault:v1:", wrappedA, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(Url("/api/v1/keys/general/rotate"), null)).StatusCode);

        var rewrap = await client.PostAsync(Url("/api/v1/keys/general/rewrap"), null);
        Assert.Equal(HttpStatusCode.OK, rewrap.StatusCode);
        var body = await rewrap.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("remaining").GetInt64());

        Assert.StartsWith("vault:v2:", await service.WrapAsync(EncryptedBlobStore.KeyName(a.Id, KeyPurpose.General), new byte[32], CancellationToken.None), StringComparison.Ordinal);
        Assert.StartsWith("vault:v1:", await service.WrapAsync(EncryptedBlobStore.KeyName(b.Id, KeyPurpose.General), new byte[32], CancellationToken.None), StringComparison.Ordinal);
        Assert.StartsWith("vault:v1:", wrappedB, StringComparison.Ordinal);

        Assert.Equal(2L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.audit_log WHERE actor_id = @Id AND action IN ('keys.rotate', 'keys.rewrap') AND outcome = 'success'", new { holder.Id }));
    }

    [Fact]
    public async Task Unknown_purpose_is_rejected()
    {
        var tenant = await data.TenantAsync();
        var holder = await data.ActorAsync(tenant, Permissions.KeysRotate);

        var response = await factory.ClientFor(holder, mfa: true).PostAsync(Url("/api/v1/keys/qualquer/rotate"), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_includes_storage_when_configured()
    {
        var response = await factory.CreateClient().GetAsync(Url("/health/ready"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Without_kms_configuration_rotation_is_unavailable_rather_than_local()
    {
        using var bare = new HubApiFactory(postgres.AppConnection);
        var tenant = await data.TenantAsync();
        var holder = await data.ActorAsync(tenant, Permissions.KeysRotate);

        var response = await bare.ClientFor(holder, mfa: true).PostAsync(Url("/api/v1/keys/general/rotate"), null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("key_service_unavailable", await response.ProblemCodeAsync());
    }

    [Fact]
    public void Production_refuses_plain_http_for_keys_and_storage()
    {
        using var production = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("ConnectionStrings:Hub", postgres.AppConnection);
            b.UseSetting("Kms:Address", "http://kms.example");
            b.UseSetting("Kms:Token", "x");
            b.UseSetting("Storage:ServiceUrl", "https://s3.example");
            b.UseSetting("Storage:Bucket", "b");
            b.UseSetting("Storage:AccessKey", "a");
            b.UseSetting("Storage:SecretKey", "s");
        });

        var error = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("HTTPS", error.ToString(), StringComparison.Ordinal);
    }
}
