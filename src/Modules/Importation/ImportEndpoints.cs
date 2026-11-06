using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

namespace Platform.Modules.Importation;

public sealed record CreateImportRequest(Guid SiteId, string? FileName, long? SizeBytes);

public sealed record CompleteUploadRequest(string? Sha256);

public sealed record UnlockPackageRequest(string? Password);

/// <param name="OriginalName">Nome saneado, so como informacao: nao decide nada.</param>
public sealed record ImportFileResponse(
    Guid Id,
    Guid? SiteId,
    string? OriginalName,
    string Status,
    long? SizeBytes,
    string? Sha256,
    string? DetectedType,
    string? RejectionCode,
    string ScanStatus,
    int? EntryCount,
    long? UncompressedBytes,
    DateTime CreatedAt,
    DateTime? InspectedAt,
    string? SourceFormat,
    string FirmwareLine,
    string? ParserStatus,
    System.Text.Json.JsonElement? Analysis);

/// <summary>Recepcao: autenticada, por unidade, em streaming e cifrada desde o primeiro byte. Nao interpreta o conteudo.</summary>
public static class ImportEndpoints
{
    private const string Columns =
        """
        id AS Id, site_id AS SiteId, original_name AS OriginalName, status AS Status, content_size_bytes AS SizeBytes,
        encode(original_sha256, 'hex') AS Sha256, detected_type AS DetectedType, rejection_code AS RejectionCode,
        scan_status AS ScanStatus, entry_count AS EntryCount, uncompressed_bytes AS UncompressedBytes,
        created_at AS CreatedAt, inspected_at AS InspectedAt, source_format AS SourceFormat,
        firmware_line AS FirmwareLine, parser_status AS ParserStatus, analysis::text AS AnalysisText
        """;

    private static readonly TimeSpan OriginalRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan StuckUpload = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim UnlockSlots = new(2, 2);
    private const int MaxUnlockAttempts = 5;
    private const long InMemoryLimit = 64 * 1024 * 1024;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/imports").WithTags("Imports");
        group.MapPost("/", CreateAsync).Produces<ImportFileResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate).RequireIdempotency();
        group.MapGet("/", ListAsync).Produces<IEnumerable<ImportFileResponse>>().RequirePermission(Permissions.ImportCreate);
        group.MapGet("/{id:guid}", GetAsync).Produces<ImportFileResponse>().RequirePermission(Permissions.ImportCreate);

        // Sem idempotencia por chave: guardar o corpo seria guardar o arquivo. Quem protege e a transicao de estado.
        group.MapPut("/{id:guid}/content", UploadAsync).Produces<ImportFileResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate);
        group.MapPost("/{id:guid}/complete-upload", CompleteAsync).Produces<ImportFileResponse>().RequirePermission(Permissions.ImportCreate).RequireIdempotency();

        // Sem idempotencia: o hash da requisicao guardaria algo derivado da senha.
        group.MapPost("/{id:guid}/unlock", UnlockAsync).Produces<ImportFileResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate);
        return routes;
    }

    private static async Task<IResult> CreateAsync(CreateImportRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, InspectionLimits limits, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if (body.SizeBytes is not { } size || size <= 0 || size > limits.MaxUploadBytes)
        {
            return Problems.Of(StatusCodes.Status413PayloadTooLarge, "file_too_large", $"O arquivo deve ter até {limits.MaxUploadBytes / (1024 * 1024)} MB.");
        }

        var name = SanitizeName(body.FileName);
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                {
                    return Problems.NotFound();
                }

                var file = await tx.QuerySingleOrDefaultAsync<ImportFileRow>(
                    $"""
                    INSERT INTO hub.import_files (tenant_id, site_id, uploaded_by, original_name, source_type, declared_size_bytes, status)
                    VALUES (@TenantId, @SiteId, @actor, @name, 'file_upload', @size, 'created')
                    RETURNING {Columns}
                    """,
                    new { tx.TenantId, body.SiteId, actor = access.Actor.ActorId, name, size });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.create", "import_file", file!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.Created($"/api/v1/imports/{file.Id}", ToResponse(file));
            },
            cancellationToken);
    }

    private static async Task<IResult> ListAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<ImportFileRow>(
                $"SELECT {Columns} FROM hub.import_files WHERE (@all OR site_id = ANY(@allowed)) AND NOT site_id = ANY(@denied) ORDER BY created_at DESC, id LIMIT 200",
                new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() }),
            cancellationToken);
        return Results.Ok(rows.Select(ToResponse));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var file = await FindAsync(database, access.Actor.TenantId, id, cancellationToken);
        return file is null || !InScope(http, file) ? Problems.NotFound() : Results.Ok(ToResponse(file));
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, InspectionLimits limits, CancellationToken cancellationToken)
    {
        if (http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return Unavailable();
        }

        var tenant = access.Actor.TenantId;
        var existing = await FindAsync(database, tenant, id, cancellationToken);
        if (existing is null || !InScope(http, existing))
        {
            return Problems.NotFound();
        }

        // Uma unica recepcao por vez. Upload interrompido pode recomecar depois de um tempo.
        var declared = await database.InTenantAsync(
            tenant,
            tx => tx.QuerySingleOrDefaultAsync<long?>(
                """
                UPDATE hub.import_files SET status = 'uploading', upload_started_at = now()
                WHERE id = @id AND (status = 'created' OR (status = 'uploading' AND upload_started_at < now() - @stuck))
                RETURNING declared_size_bytes
                """,
                new { id, stuck = StuckUpload }),
            cancellationToken);

        if (declared is not { } declaredSize)
        {
            return Problems.Conflict("Este arquivo já foi enviado ou está sendo enviado.");
        }

        // O limite geral da API e de 1 MiB; aqui ele sobe so ate o tamanho declarado. O excedente e cortado na leitura.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = declaredSize + 1;
        }

        BlobReference reference;
        try
        {
            await using var limited = new LimitedReadStream(http.Request.Body, declaredSize);
            reference = await store.StoreAsync(tenant, BlobKind.ImportOriginal, BlobClassification.Personal, limited, access.Actor.ActorId, DateTimeOffset.UtcNow.Add(OriginalRetention), cancellationToken);
        }
        catch (Exception ex) when (ex is UploadTooLargeException or BadHttpRequestException or KeyServiceException or IOException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonClientException or OperationCanceledException)
        {
            await database.InTenantAsync(tenant, tx => tx.ExecuteAsync("UPDATE hub.import_files SET status = 'created', upload_started_at = NULL WHERE id = @id AND status = 'uploading'", new { id }), CancellationToken.None);
            return ex switch
            {
                UploadTooLargeException or BadHttpRequestException => Problems.Of(StatusCodes.Status413PayloadTooLarge, "file_too_large", "O arquivo é maior que o tamanho informado."),
                OperationCanceledException => Problems.Of(StatusCodes.Status400BadRequest, "upload_interrupted", "O envio foi interrompido. Envie o arquivo de novo."),
                _ => Unavailable(),
            };
        }

        var file = await database.InTenantAsync(
            tenant,
            async tx =>
            {
                var updated = await tx.QuerySingleOrDefaultAsync<ImportFileRow>(
                    $"""
                    UPDATE hub.import_files
                    SET status = 'uploaded', encrypted_blob_ref = @blob, original_sha256 = @digest, content_size_bytes = @size, encryption_key_ref = 'envelope'
                    WHERE id = @id
                    RETURNING {Columns}
                    """,
                    new { blob = reference.Id.ToString(), digest = reference.PlaintextSha256, size = reference.PlaintextSize, id });
                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, "import.upload", "import_file", id.ToString(), AuditWriter.Success, http.GetCorrelationId(),
                    Metadata: new { sha256 = Convert.ToHexStringLower(reference.PlaintextSha256), size = reference.PlaintextSize }));
                return updated;
            },
            CancellationToken.None);

        return Results.Ok(ToResponse(file!));
    }

    private static async Task<IResult> CompleteAsync(Guid id, CompleteUploadRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.Sha256 is not { Length: 64 } declared || !declared.All(Uri.IsHexDigit))
        {
            return Problems.Invalid("Informe o SHA-256 do arquivo, com 64 caracteres hexadecimais.");
        }

        var existing = await FindAsync(database, access.Actor.TenantId, id, cancellationToken);
        if (existing is null || !InScope(http, existing))
        {
            return Problems.NotFound();
        }

        if (existing.Status != "uploaded")
        {
            return Problems.Conflict("O envio deste arquivo ainda não terminou ou já foi concluído.");
        }

        var hashMatches = string.Equals(existing.Sha256, declared, StringComparison.OrdinalIgnoreCase);
        var store = http.RequestServices.GetService<EncryptedBlobStore>();

        var file = await database.InTenantAsync(
            access.Actor.TenantId,
            async tx =>
            {
                var updated = await tx.QuerySingleOrDefaultAsync<ImportFileRow>(
                    $"""
                    UPDATE hub.import_files
                    SET status = @status, rejection_code = @rejection
                    WHERE id = @id AND status = 'uploaded'
                    RETURNING {Columns}
                    """,
                    new { status = hashMatches ? "quarantined" : "rejected", rejection = hashMatches ? null : RejectionCodes.HashMismatch, id });
                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, hashMatches ? "import.quarantined" : "import.rejected", "import_file", id.ToString(), hashMatches ? AuditWriter.Success : AuditWriter.Denied, http.GetCorrelationId()));
                return updated;
            },
            cancellationToken);

        if (!hashMatches && store is not null && await BlobOfAsync(database, access.Actor.TenantId, id, cancellationToken) is { } blob)
        {
            await store.DestroyAsync(access.Actor.TenantId, blob, CancellationToken.None);
        }

        return file is null ? Problems.Conflict("O envio deste arquivo já foi concluído.") : Results.Ok(ToResponse(file));
    }

    private static async Task<IResult> UnlockAsync(Guid id, UnlockPackageRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(body.Password))
        {
            return Problems.Invalid("Informe a senha do pacote.");
        }

        if (http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return Unavailable();
        }

        var tenant = access.Actor.TenantId;
        var existing = await FindAsync(database, tenant, id, cancellationToken);
        if (existing is null || !InScope(http, existing))
        {
            return Problems.NotFound();
        }

        // Argon2 consome muita memoria de proposito: poucas aberturas simultaneas, ou vira negacao de servico.
        if (!await UnlockSlots.WaitAsync(0, cancellationToken))
        {
            return Problems.Of(StatusCodes.Status429TooManyRequests, "rate_limited", "Muitas aberturas em andamento. Tente de novo em instantes.");
        }

        try
        {
            // Conta antes de derivar: tentativas em paralelo nao furam o limite.
            var attempts = await database.InTenantAsync(
                tenant,
                tx => tx.QuerySingleOrDefaultAsync<int?>(
                    "UPDATE hub.import_files SET unlock_attempts = unlock_attempts + 1 WHERE id = @id AND status = 'awaiting_password' AND unlock_attempts < @max RETURNING unlock_attempts",
                    new { id, max = MaxUnlockAttempts }),
                cancellationToken);

            if (attempts is not { } attempt)
            {
                return Problems.Conflict("Este arquivo não está aguardando senha.");
            }

            var blob = await BlobOfAsync(database, tenant, id, cancellationToken);
            if (blob is not { } packageBlob)
            {
                await RefundAttemptAsync(database, tenant, id);
                return Unavailable();
            }

            await using var package = NewBuffer(existing.SizeBytes ?? 0);
            await using var plain = NewBuffer(existing.SizeBytes ?? 0);
            try
            {
                if (!await store.ReadAsync(tenant, packageBlob, package, cancellationToken))
                {
                    await RefundAttemptAsync(database, tenant, id);
                    return Unavailable();
                }

                package.Position = 0;
                await HubPackage.OpenAsync(package, plain, body.Password, cancellationToken);
            }
            catch (PackageOpenException)
            {
                return await FailUnlockAsync(http, access, database, store, id, packageBlob, attempt);
            }
            catch (Exception ex) when (ex is KeyServiceException or BlobIntegrityException or IOException or OperationCanceledException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonClientException)
            {
                // A senha nao chegou a ser julgada: a falha e nossa, nao conta contra quem tentou.
                await RefundAttemptAsync(database, tenant, id);
                return Unavailable();
            }

            // O conteudo volta ao inicio da fila como qualquer arquivo novo: inspecao e antivirus valem para ele tambem.
            plain.Position = 0;
            BlobReference stored;
            try
            {
                stored = await store.StoreAsync(tenant, BlobKind.ImportOriginal, BlobClassification.Personal, plain, access.Actor.ActorId, DateTimeOffset.UtcNow.Add(OriginalRetention), CancellationToken.None);
            }
            catch (Exception ex) when (ex is KeyServiceException or IOException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonClientException)
            {
                await RefundAttemptAsync(database, tenant, id);
                return Unavailable();
            }

            var file = await database.InTenantAsync(
                tenant,
                async tx =>
                {
                    var updated = await tx.QuerySingleOrDefaultAsync<ImportFileRow>(
                        $"""
                        UPDATE hub.import_files
                        SET status = 'quarantined', encrypted_blob_ref = @blob, content_sha256 = @digest, content_size_bytes = @size,
                            unlocked_from_package = true, inspection_attempts = 0, scan_status = 'not_scanned'
                        WHERE id = @id AND status = 'awaiting_password'
                        RETURNING {Columns}
                        """,
                        new { blob = stored.Id.ToString(), digest = stored.PlaintextSha256, size = stored.PlaintextSize, id });
                    if (updated is not null)
                    {
                        await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.unlock", "import_file", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    }

                    return updated;
                },
                CancellationToken.None);

            // Outra abertura venceu a corrida: esta copia em claro nao tem dono.
            if (file is null)
            {
                await store.DestroyAsync(tenant, stored.Id, CancellationToken.None);
                return Problems.Conflict("Este arquivo não está aguardando senha.");
            }

            await store.DestroyAsync(tenant, packageBlob, CancellationToken.None);
            return Results.Ok(ToResponse(file));
        }
        finally
        {
            UnlockSlots.Release();
        }
    }

    private static Task<int> RefundAttemptAsync(ITenantDatabase database, Guid tenant, Guid id) =>
        database.InTenantAsync(
            tenant,
            tx => tx.ExecuteAsync("UPDATE hub.import_files SET unlock_attempts = unlock_attempts - 1 WHERE id = @id AND status = 'awaiting_password' AND unlock_attempts > 0", new { id }),
            CancellationToken.None);

    private static async Task<IResult> FailUnlockAsync(HttpContext http, RequestAccess access, ITenantDatabase database, EncryptedBlobStore store, Guid id, Guid blob, int attempt)
    {
        var exhausted = attempt >= MaxUnlockAttempts;
        await database.InTenantAsync(
            access.Actor.TenantId,
            async tx =>
            {
                if (exhausted)
                {
                    await tx.ExecuteAsync("UPDATE hub.import_files SET status = 'rejected', rejection_code = @code WHERE id = @id", new { code = RejectionCodes.TooManyUnlockAttempts, id });
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.unlock", "import_file", id.ToString(), AuditWriter.Denied, http.GetCorrelationId(), Metadata: new { attempt }));
                return true;
            },
            CancellationToken.None);

        if (exhausted)
        {
            await store.DestroyAsync(access.Actor.TenantId, blob, CancellationToken.None);
        }

        return Problems.Of(StatusCodes.Status422UnprocessableEntity, "package_open_failed", new PackageOpenException().Message);
    }

    private static Stream NewBuffer(long size) => size <= InMemoryLimit
        ? new MemoryStream()
        : new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    private static async Task<Guid?> BlobOfAsync(ITenantDatabase database, Guid tenant, Guid id, CancellationToken cancellationToken)
    {
        var text = await database.InTenantAsync(tenant, tx => tx.QuerySingleOrDefaultAsync<string?>("SELECT encrypted_blob_ref FROM hub.import_files WHERE id = @id", new { id }), cancellationToken);
        return Guid.TryParse(text, out var blob) ? blob : null;
    }

    private static Task<ImportFileRow?> FindAsync(ITenantDatabase database, Guid tenant, Guid id, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenant, tx => tx.QuerySingleOrDefaultAsync<ImportFileRow>($"SELECT {Columns} FROM hub.import_files WHERE id = @id", new { id }), cancellationToken);

    private static bool InScope(HttpContext http, ImportFileRow file) =>
        file.SiteId is { } site && http.GetScope().Includes(site);

    private static ImportFileResponse ToResponse(ImportFileRow row)
    {
        System.Text.Json.JsonElement? analysis = null;
        if (row.AnalysisText is not null)
        {
            using var document = System.Text.Json.JsonDocument.Parse(row.AnalysisText);
            analysis = document.RootElement.Clone();
        }

        return new ImportFileResponse(row.Id, row.SiteId, row.OriginalName, row.Status, row.SizeBytes, row.Sha256, row.DetectedType, row.RejectionCode, row.ScanStatus, row.EntryCount, row.UncompressedBytes, row.CreatedAt, row.InspectedAt, row.SourceFormat, row.FirmwareLine, row.ParserStatus, analysis);
    }

    private static IResult Unavailable() =>
        Problems.Of(StatusCodes.Status503ServiceUnavailable, "storage_unavailable", "Armazenamento indisponível. O arquivo não foi recebido.");

    /// <summary>Nunca e usado como caminho. Sem pasta, sem controle, limitado.</summary>
    internal static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var last = name.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;
        var clean = new string(last.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length <= 255 ? clean : clean[..255];
    }
}

internal sealed record ImportFileRow(
    Guid Id,
    Guid? SiteId,
    string? OriginalName,
    string Status,
    long? SizeBytes,
    string? Sha256,
    string? DetectedType,
    string? RejectionCode,
    string ScanStatus,
    int? EntryCount,
    long? UncompressedBytes,
    DateTime CreatedAt,
    DateTime? InspectedAt,
    string? SourceFormat,
    string FirmwareLine,
    string? ParserStatus,
    string? AnalysisText);

internal sealed class UploadTooLargeException : Exception
{
    public UploadTooLargeException()
        : base("O envio excedeu o tamanho informado.")
    {
    }

    public UploadTooLargeException(string message)
        : base(message)
    {
    }

    public UploadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Corta o envio no tamanho declarado, mesmo em servidores sem limite proprio.</summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long total;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        total += read;
        return total > limit ? throw new UploadTooLargeException() : read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use a leitura assincrona.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
