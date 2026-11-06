using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Modules.Importation;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests;

internal sealed class FakeScanner(ScanResult result) : IMalwareScanner
{
    public int Calls { get; private set; }

    public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

/// <summary>Upload em Kestrel real, armazenamento e KMS reais, com o papel do worker. Antivirus simulado aqui.</summary>
[Collection(PostgresTests.Name)]
public sealed class ImportQuarantineTests(PostgresFixture postgres, StorageFixture storage) : IDisposable
{
    private readonly TestData data = new(postgres.AdminOnHubConnection);
    private readonly EdgeHost host = new(postgres, storage);
    private readonly HttpClient kms = storage.CreateKmsClient();
    private readonly Amazon.S3.IAmazonS3 s3 = storage.CreateS3Client();
    private readonly NpgsqlDataSource workerSource = NpgsqlDataSource.Create(postgres.WorkerImportConnection);

    public void Dispose()
    {
        kms.Dispose();
        s3.Dispose();
        workerSource.Dispose();
        host.Dispose();
    }

    private static CancellationToken None => CancellationToken.None;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static byte[] ValidZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var csv = archive.CreateEntry("users.csv").Open())
            {
                csv.Write("id;name\n1;MARCADORPESSOAL\n"u8);
            }

            using var image = archive.CreateEntry("user_images/1.jpg").Open();
            image.Write(RandomNumberGenerator.GetBytes(4096));
        }

        return buffer.ToArray();
    }

    private ImportInspectionService Worker(IMalwareScanner scanner) => new(
        workerSource,
        new TenantDatabase(workerSource),
        new EncryptedBlobStore(new S3ObjectStore(s3, StorageFixture.Bucket), new TransitKeyService(kms), new TenantDatabase(workerSource)),
        scanner,
        new InspectionLimits(),
        NullLogger<ImportInspectionService>.Instance);

    private async Task<(HttpClient Client, Guid Site, Guid Tenant, string Slug)> OperatorAsync(params string[] permissions)
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var granted = permissions.Length == 0 ? [Permissions.ImportCreate] : permissions;
        return (host.AdminClient(await data.ActorAsync(tenant, granted)), site, tenant.Id, tenant.Slug);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, Guid site, long size, string name = "users.zip")
    {
        var response = await client.PostAsJsonAsync(Url("/api/v1/imports"), new { siteId = site, fileName = name, sizeBytes = size });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, byte[] content) =>
        client.PutAsync(Url($"/api/v1/imports/{id}/content"), new ByteArrayContent(content));

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid id, byte[] content) =>
        client.PostAsJsonAsync(Url($"/api/v1/imports/{id}/complete-upload"), new { sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) });

    private static async Task<Guid> UploadAsync(HttpClient client, Guid site, byte[] content)
    {
        var id = await CreateAsync(client, site, content.Length);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, id, content)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, id, content)).StatusCode);
        return id;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync(Url($"/api/v1/imports/{id}"))).Content.ReadFromJsonAsync<JsonElement>();

    private static async Task DrainQueueAsync(ImportInspectionService worker)
    {
        // A fila e compartilhada: inspeciona ate este teste nao ter mais o que esperar.
        for (var i = 0; i < 25 && await worker.ProcessNextAsync(None); i++)
        {
        }
    }

    // Pacote proprio

    private const string PackagePassword = "senha-do-pacote-MARCADORSENHA";

    private static async Task<byte[]> SealAsync(byte[] content)
    {
        using var output = new MemoryStream();
        await HubPackage.SealAsync(new MemoryStream(content), output, PackagePassword, None);
        return output.ToArray();
    }

    private static Task<HttpResponseMessage> UnlockAsync(HttpClient client, Guid id, string password) =>
        client.PostAsJsonAsync(Url($"/api/v1/imports/{id}/unlock"), new { password });

    private async Task<Guid> PackageAwaitingPasswordAsync(HttpClient client, Guid site, byte[] inner)
    {
        var id = await UploadAsync(client, site, await SealAsync(inner));
        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));
        Assert.Equal("awaiting_password", (await GetAsync(client, id)).GetProperty("status").GetString());
        return id;
    }

    private async Task<string> BlobRefAsync(Guid tenant, Guid id)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT encrypted_blob_ref FROM hub.import_files WHERE tenant_id = @t AND id = @i", connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("i", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> BlobReadableAsync(Guid tenant, string blob)
    {
        var store = new EncryptedBlobStore(new S3ObjectStore(s3, StorageFixture.Bucket), new TransitKeyService(kms), new TenantDatabase(workerSource));
        using var sink = new MemoryStream();
        return await store.ReadAsync(tenant, Guid.Parse(blob), sink, None);
    }

    [Fact]
    public async Task Package_is_never_scanned_while_encrypted_and_the_content_is_inspected_after_opening()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, await SealAsync(ValidZip()));

        var closed = new FakeScanner(new ScanResult(ScanVerdict.Clean));
        await DrainQueueAsync(Worker(closed));

        var waiting = await GetAsync(client, id);
        Assert.Equal("awaiting_password", waiting.GetProperty("status").GetString());
        Assert.Equal(DetectedTypes.HubPackage, waiting.GetProperty("detectedType").GetString());
        Assert.Equal(0, closed.Calls);
        var packageBlob = await BlobRefAsync(tenant, id);

        var opened = await UnlockAsync(client, id, PackagePassword);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("quarantined", (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // A copia cifrada com a senha do usuario deixa de existir.
        Assert.False(await BlobReadableAsync(tenant, packageBlob));

        var scanner = new FakeScanner(new ScanResult(ScanVerdict.Clean));
        await DrainQueueAsync(Worker(scanner));

        var file = await GetAsync(client, id);
        Assert.Equal("inspected", file.GetProperty("status").GetString());
        Assert.Equal("zip", file.GetProperty("detectedType").GetString());
        Assert.Equal(1, scanner.Calls);
        Assert.Equal(SourceFormats.Users, file.GetProperty("sourceFormat").GetString());
        Assert.Equal("undetermined", file.GetProperty("firmwareLine").GetString());
        Assert.Equal(ParserStatuses.NeedsManualMapping, file.GetProperty("parserStatus").GetString());
    }

    [Fact]
    public async Task Malware_inside_a_package_is_caught_after_opening()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        var blob = await BlobRefAsync(tenant, id);

        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Infected, "Teste.EICAR"))));

        Assert.Equal(RejectionCodes.Malware, (await GetAsync(client, id)).GetProperty("rejectionCode").GetString());
        Assert.False(await BlobReadableAsync(tenant, blob));
    }

    [Fact]
    public async Task Package_inside_a_package_is_rejected()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, await SealAsync(ValidZip()));
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(client, id, PackagePassword)).StatusCode);

        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));

        var file = await GetAsync(client, id);
        Assert.Equal("rejected", file.GetProperty("status").GetString());
        Assert.Equal(RejectionCodes.NestedPackage, file.GetProperty("rejectionCode").GetString());
    }

    [Fact]
    public async Task Repeated_wrong_passwords_destroy_the_package_and_the_right_one_no_longer_works()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        var blob = await BlobRefAsync(tenant, id);

        for (var i = 0; i < 5; i++)
        {
            var wrong = await UnlockAsync(client, id, $"senha-errada-numero-{i}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
            Assert.Equal("package_open_failed", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var file = await GetAsync(client, id);
        Assert.Equal("rejected", file.GetProperty("status").GetString());
        Assert.Equal(RejectionCodes.TooManyUnlockAttempts, file.GetProperty("rejectionCode").GetString());
        Assert.False(await BlobReadableAsync(tenant, blob));
        Assert.Equal(HttpStatusCode.Conflict, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
    }

    [Fact]
    public async Task Fewer_than_the_limit_of_wrong_passwords_still_allows_opening()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UnlockAsync(client, id, "senha-errada-qualquer")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));
    }

    [Fact]
    public async Task Ordinary_file_cannot_be_unlocked()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, ValidZip());

        Assert.Equal(HttpStatusCode.Conflict, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));
    }

    [Fact]
    public async Task Password_and_cell_values_are_not_persisted_anywhere()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UnlockAsync(client, id, "MARCADORSENHA-errada-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));

        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        foreach (var table in new[] { "import_files", "audit_log", "idempotency_keys" })
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM hub.{table} t WHERE t.tenant_id = @t AND (t::text LIKE '%MARCADORSENHA%' OR t::text LIKE '%MARCADORPESSOAL%')", connection);
            command.Parameters.AddWithValue("t", tenant);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        var analysis = (await GetAsync(client, id)).GetProperty("analysis");
        Assert.Equal(2, analysis.GetProperty("columnCount").GetInt32());
        Assert.Equal(1, analysis.GetProperty("dataRows").GetInt64());
    }

    [Fact]
    public async Task Other_tenant_and_other_site_cannot_try_a_password()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());

        var (otherTenant, _, _, _) = await OperatorAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await UnlockAsync(otherTenant, id, PackagePassword)).StatusCode);

        // Essas tentativas nao gastam o limite do dono: ele ainda abre.
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));
    }

    private async Task SetCounterAsync(Guid tenant, Guid id, string column, int value)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"UPDATE hub.import_files SET {column} = @v WHERE tenant_id = @t AND id = @i", connection);
        command.Parameters.AddWithValue("v", value);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("i", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> UnlockAttemptsAsync(Guid tenant, Guid id)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT unlock_attempts FROM hub.import_files WHERE tenant_id = @t AND id = @i", connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("i", id);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Same_tenant_other_site_cannot_try_a_password_nor_spend_attempts()
    {
        var (client, site, tenant, slug) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());

        var otherSite = await data.SiteAsync(tenant, "Outra");
        var scoped = await data.ActorAsync((tenant, slug));
        await data.GrantAsync(scoped, Permissions.ImportCreate, otherSite);
        using var limited = host.AdminClient(scoped);

        Assert.Equal(HttpStatusCode.NotFound, (await UnlockAsync(limited, id, PackagePassword)).StatusCode);
        Assert.Equal("awaiting_password", (await GetAsync(client, id)).GetProperty("status").GetString());
        Assert.Equal(0, await UnlockAttemptsAsync(tenant, id));
    }

    [Fact]
    public async Task Exhausted_counter_refuses_even_the_right_password()
    {
        // Duas tentativas simultaneas na ultima vaga: a segunda nao chega a derivar.
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        await SetCounterAsync(tenant, id, "unlock_attempts", 5);

        Assert.Equal(HttpStatusCode.Conflict, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
    }

    [Fact]
    public async Task Storage_failure_during_unlock_does_not_spend_an_attempt()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        var store = new EncryptedBlobStore(new S3ObjectStore(s3, StorageFixture.Bucket), new TransitKeyService(kms), new TenantDatabase(workerSource));
        await store.DestroyAsync(tenant, Guid.Parse(await BlobRefAsync(tenant, id)), None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        Assert.Equal(0, await UnlockAttemptsAsync(tenant, id));
    }

    [Fact]
    public async Task Inspection_attempts_of_the_package_do_not_count_against_its_content()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        await SetCounterAsync(tenant, id, "inspection_attempts", 3);

        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(client, id, PackagePassword)).StatusCode);
        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));

        Assert.Equal("inspected", (await GetAsync(client, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unlock_requires_permission_and_a_password()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await PackageAwaitingPasswordAsync(client, site, ValidZip());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Url($"/api/v1/imports/{id}/unlock"), new { password = "" })).StatusCode);

        var (reader, _, _, _) = await OperatorAsync(Permissions.GatewaysRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await UnlockAsync(reader, id, PackagePassword)).StatusCode);
    }

    // Caminho normal

    [Fact]
    public async Task Clean_file_goes_from_upload_to_inspected()
    {
        var (client, site, _, _) = await OperatorAsync();
        var content = ValidZip();

        var id = await UploadAsync(client, site, content);
        Assert.Equal("quarantined", (await GetAsync(client, id)).GetProperty("status").GetString());

        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));

        var file = await GetAsync(client, id);
        Assert.Equal("inspected", file.GetProperty("status").GetString());
        Assert.Equal("clean", file.GetProperty("scanStatus").GetString());
        Assert.Equal("zip", file.GetProperty("detectedType").GetString());
        Assert.Equal(2, file.GetProperty("entryCount").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), file.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Stored_object_is_encrypted_and_the_original_can_be_read_back()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var content = ValidZip();
        var id = await UploadAsync(client, site, content);

        var blob = await data.ScalarAsync<string>("SELECT encrypted_blob_ref FROM hub.import_files WHERE id = @id", new { id });
        var key = await data.ScalarAsync<string>("SELECT object_key FROM hub.blob_objects WHERE id = @blob::uuid", new { blob });

        using var raw = await s3.GetObjectAsync(StorageFixture.Bucket, key);
        using var stored = new MemoryStream();
        await raw.ResponseStream.CopyToAsync(stored);
        Assert.Equal(-1, stored.ToArray().AsSpan().IndexOf("PK\u0003\u0004"u8));

        using var plain = new MemoryStream();
        var store = new EncryptedBlobStore(new S3ObjectStore(s3, StorageFixture.Bucket), new TransitKeyService(kms), new TenantDatabase(workerSource));
        Assert.True(await store.ReadAsync(tenant, Guid.Parse(blob), plain, None));
        Assert.Equal(content, plain.ToArray());
    }

    [Fact]
    public async Task Upload_larger_than_the_general_api_limit_works_up_to_the_declared_size()
    {
        var (client, site, _, _) = await OperatorAsync();
        var content = new byte[3 * 1024 * 1024];
        Encoding.ASCII.GetBytes("a;b\n").CopyTo(content, 0);
        Array.Fill(content, (byte)'x', 4, content.Length - 4);

        var id = await UploadAsync(client, site, content);

        Assert.Equal(content.Length, (await GetAsync(client, id)).GetProperty("sizeBytes").GetInt64());
    }

    // Recusas pelo envio

    [Fact]
    public async Task Sending_more_than_declared_is_cut_and_nothing_stays_stored()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await CreateAsync(client, site, 1024);

        var response = await PutAsync(client, id, new byte[2 * 1024 * 1024]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("created", (await GetAsync(client, id)).GetProperty("status").GetString());
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.blob_objects WHERE tenant_id = @tenant AND destroyed_at IS NULL", new { tenant }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(300_000_000)]
    public async Task Declared_size_out_of_range_is_refused_before_any_byte(long size)
    {
        var (client, site, _, _) = await OperatorAsync();

        var response = await client.PostAsJsonAsync(Url("/api/v1/imports"), new { siteId = site, fileName = "a.zip", sizeBytes = size });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_hash_is_rejected_and_the_content_destroyed()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var content = ValidZip();
        var id = await CreateAsync(client, site, content.Length);
        await PutAsync(client, id, content);

        var response = await client.PostAsJsonAsync(Url($"/api/v1/imports/{id}/complete-upload"), new { sha256 = new string('0', 64) });

        var file = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected", file.GetProperty("status").GetString());
        Assert.Equal("hash_mismatch", file.GetProperty("rejectionCode").GetString());
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.blob_objects WHERE tenant_id = @tenant AND destroyed_at IS NULL", new { tenant }));
    }

    [Fact]
    public async Task Second_upload_of_the_same_import_is_a_conflict()
    {
        var (client, site, _, _) = await OperatorAsync();
        var content = ValidZip();
        var id = await CreateAsync(client, site, content.Length);
        await PutAsync(client, id, content);

        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, id, content)).StatusCode);
    }

    [Fact]
    public async Task Complete_before_upload_is_a_conflict()
    {
        var (client, site, _, _) = await OperatorAsync();
        var content = ValidZip();
        var id = await CreateAsync(client, site, content.Length);

        Assert.Equal(HttpStatusCode.Conflict, (await CompleteAsync(client, id, content)).StatusCode);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\evil.exe", "evil.exe")]
    [InlineData("users.zip", "users.zip")]
    public async Task File_name_is_sanitized(string sent, string stored)
    {
        var (client, site, _, _) = await OperatorAsync();

        var id = await CreateAsync(client, site, 10, sent);

        Assert.Equal(stored, (await GetAsync(client, id)).GetProperty("originalName").GetString());
    }

    // Recusas na inspecao

    [Fact]
    public async Task Malicious_zip_is_rejected_destroyed_and_never_reaches_the_scanner()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("../../fora.txt").Open();
            entry.Write("a;b\n"u8);
        }

        var id = await UploadAsync(client, site, buffer.ToArray());
        var scanner = new FakeScanner(new ScanResult(ScanVerdict.Clean));

        await DrainQueueAsync(Worker(scanner));

        var file = await GetAsync(client, id);
        Assert.Equal("rejected", file.GetProperty("status").GetString());
        Assert.Equal("unsafe_path", file.GetProperty("rejectionCode").GetString());
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.blob_objects WHERE tenant_id = @tenant AND destroyed_at IS NULL", new { tenant }));
        Assert.Equal(1L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.audit_log WHERE tenant_id = @tenant AND action = 'import.rejected'", new { tenant }));
    }

    [Fact]
    public async Task Infected_file_is_rejected_with_the_signature_and_destroyed()
    {
        var (client, site, tenant, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, ValidZip());

        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Infected, "Eicar-Test-Signature"))));

        var file = await GetAsync(client, id);
        Assert.Equal("rejected", file.GetProperty("status").GetString());
        Assert.Equal("malware_detected", file.GetProperty("rejectionCode").GetString());
        Assert.Equal("infected", file.GetProperty("scanStatus").GetString());
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.blob_objects WHERE tenant_id = @tenant AND destroyed_at IS NULL", new { tenant }));
    }

    [Fact]
    public async Task Scanner_down_keeps_the_file_in_quarantine_and_never_passes_it()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, ValidZip());
        var worker = Worker(new FakeScanner(new ScanResult(ScanVerdict.Unavailable)));

        await worker.ProcessNextAsync(None);
        while (await data.ScalarAsync<string>("SELECT status FROM hub.import_files WHERE id = @id", new { id }) == "quarantined"
            && await data.ScalarAsync<int>("SELECT inspection_attempts FROM hub.import_files WHERE id = @id", new { id }) == 0)
        {
            await worker.ProcessNextAsync(None);
        }

        var file = await GetAsync(client, id);
        Assert.Equal("quarantined", file.GetProperty("status").GetString());
        Assert.Equal("unavailable", file.GetProperty("scanStatus").GetString());
    }

    [Fact]
    public async Task Repeated_scanner_failures_end_in_rejection_not_in_approval()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, ValidZip());
        var worker = Worker(new FakeScanner(new ScanResult(ScanVerdict.Unavailable)));

        for (var i = 0; i < 40 && await data.ScalarAsync<string>("SELECT status FROM hub.import_files WHERE id = @id", new { id }) == "quarantined"; i++)
        {
            await worker.ProcessNextAsync(None);
        }

        var file = await GetAsync(client, id);
        Assert.Equal("rejected", file.GetProperty("status").GetString());
        Assert.Equal("inspection_failed", file.GetProperty("rejectionCode").GetString());
    }

    [Fact]
    public async Task Interrupted_inspection_returns_to_the_queue()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, ValidZip());
        await data.ExecuteAsync("UPDATE hub.import_files SET status = 'inspecting', inspection_started_at = now() - interval '1 hour', inspection_attempts = 1 WHERE id = @id", new { id });

        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));

        Assert.Equal("inspected", (await GetAsync(client, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inspection_in_progress_is_not_taken_by_a_second_worker()
    {
        var (client, site, _, _) = await OperatorAsync();
        var id = await UploadAsync(client, site, ValidZip());
        await data.ExecuteAsync("UPDATE hub.import_files SET status = 'inspecting', inspection_started_at = now(), inspection_attempts = 1 WHERE id = @id", new { id });

        await DrainQueueAsync(Worker(new FakeScanner(new ScanResult(ScanVerdict.Clean))));

        Assert.Equal("inspecting", (await GetAsync(client, id)).GetProperty("status").GetString());
    }

    // Isolamento

    [Fact]
    public async Task Other_tenant_and_other_site_get_not_found()
    {
        var (client, site, tenant, slug) = await OperatorAsync();
        var content = ValidZip();
        var id = await CreateAsync(client, site, content.Length);

        var (stranger, _, _, _) = await OperatorAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Url($"/api/v1/imports/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(stranger, id, content)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(stranger, id, content)).StatusCode);

        var otherSite = await data.SiteAsync(tenant, "Outra");
        var scoped = await data.ActorAsync((tenant, slug));
        await data.GrantAsync(scoped, Permissions.ImportCreate, otherSite);
        using var limited = host.AdminClient(scoped);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/imports/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.PostAsJsonAsync(Url("/api/v1/imports"), new { siteId = site, fileName = "a", sizeBytes = 10 })).StatusCode);

        // Mesmo tenant, outra unidade: aqui o RLS nao ajuda, e so a checagem de escopo impede.
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(limited, id, content)).StatusCode);
        Assert.Equal("created", (await GetAsync(client, id)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, id, content)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(limited, id, content)).StatusCode);
        Assert.Equal("uploaded", (await GetAsync(client, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missing_permission_is_refused()
    {
        var (client, site, _, _) = await OperatorAsync(Permissions.SitesRead);

        var response = await client.PostAsJsonAsync(Url("/api/v1/imports"), new { siteId = site, fileName = "a", sizeBytes = 10 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_storage_upload_is_unavailable_rather_than_kept_in_plain_text()
    {
        using var bare = new HubApiFactory(postgres.AppConnection);
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var client = bare.ClientFor(await data.ActorAsync(tenant, Permissions.ImportCreate));
        var id = await CreateAsync(client, site, 10);

        var response = await client.PutAsync(Url($"/api/v1/imports/{id}/content"), new ByteArrayContent(new byte[10]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // Papeis de banco

    [Fact]
    public async Task Api_role_cannot_claim_the_inspection_queue()
    {
        await using var connection = new NpgsqlConnection(postgres.AppConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM hub.claim_next_import(interval '1 hour')", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteReaderAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Worker_role_cannot_read_tenant_data_without_tenant_context()
    {
        var (client, site, _, _) = await OperatorAsync();
        await CreateAsync(client, site, 10);
        await using var connection = await workerSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM hub.import_files", connection);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}

/// <summary>Protocolo INSTREAM contra um servidor local simulado. O ClamAV real e exercitado a parte.</summary>
public sealed class ClamAvProtocolTests
{
    private static async Task<(ScanResult Result, byte[] Received)> ScanAgainstAsync(string reply, byte[] content, bool close = false)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var received = new MemoryStream();

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var network = client.GetStream();
            if (close)
            {
                return;
            }

            var command = new byte[10];
            await network.ReadExactlyAsync(command);
            Assert.Equal("zINSTREAM\0"u8.ToArray(), command);
            var header = new byte[4];
            while (true)
            {
                await network.ReadExactlyAsync(header);
                var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
                if (size == 0)
                {
                    break;
                }

                var chunk = new byte[size];
                await network.ReadExactlyAsync(chunk);
                received.Write(chunk);
            }

            await network.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });

        var result = await new ClamAvScanner("127.0.0.1", port).ScanAsync(new MemoryStream(content), CancellationToken.None);
        await server;
        return (result, received.ToArray());
    }

    [Fact]
    public async Task Clean_reply_is_clean_and_all_bytes_are_sent_in_chunks()
    {
        var content = RandomNumberGenerator.GetBytes(200_000);

        var (result, received) = await ScanAgainstAsync("stream: OK\0", content);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal(content, received);
    }

    [Fact]
    public async Task Found_reply_returns_the_signature()
    {
        var (result, _) = await ScanAgainstAsync("stream: Win.Test.EICAR_HDB-1 FOUND\0", [1, 2, 3]);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Win.Test.EICAR_HDB-1", result.Signature);
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR\0")]
    [InlineData("resposta inesperada\0")]
    [InlineData("")]
    public async Task Anything_else_is_never_clean(string reply)
    {
        var (result, _) = await ScanAgainstAsync(reply, [1, 2, 3]);

        Assert.Equal(ScanVerdict.Unavailable, result.Verdict);
    }

    [Fact]
    public async Task Connection_refused_is_unavailable()
    {
        var result = await new ClamAvScanner("127.0.0.1", 1).ScanAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.Equal(ScanVerdict.Unavailable, result.Verdict);
    }
}
