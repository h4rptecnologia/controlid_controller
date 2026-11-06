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
    DateTime? InspectedAt);

/// <summary>Recepcao: autenticada, por unidade, em streaming e cifrada desde o primeiro byte. Nao interpreta o conteudo.</summary>
public static class ImportEndpoints
{
    private const string Columns =
        """
        id AS Id, site_id AS SiteId, original_name AS OriginalName, status AS Status, content_size_bytes AS SizeBytes,
        encode(original_sha256, 'hex') AS Sha256, detected_type AS DetectedType, rejection_code AS RejectionCode,
        scan_status AS ScanStatus, entry_count AS EntryCount, uncompressed_bytes AS UncompressedBytes,
        created_at AS CreatedAt, inspected_at AS InspectedAt
        """;

    private static readonly TimeSpan OriginalRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan StuckUpload = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/imports").WithTags("Imports");
        group.MapPost("/", CreateAsync).Produces<ImportFileResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate).RequireIdempotency();
        group.MapGet("/", ListAsync).Produces<IEnumerable<ImportFileResponse>>().RequirePermission(Permissions.ImportCreate);
        group.MapGet("/{id:guid}", GetAsync).Produces<ImportFileResponse>().RequirePermission(Permissions.ImportCreate);

        // Sem idempotencia por chave: guardar o corpo seria guardar o arquivo. Quem protege e a transicao de estado.
        group.MapPut("/{id:guid}/content", UploadAsync).Produces<ImportFileResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate);
        group.MapPost("/{id:guid}/complete-upload", CompleteAsync).Produces<ImportFileResponse>().RequirePermission(Permissions.ImportCreate).RequireIdempotency();
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

                var file = await tx.QuerySingleOrDefaultAsync<ImportFileResponse>(
                    $"""
                    INSERT INTO hub.import_files (tenant_id, site_id, uploaded_by, original_name, source_type, declared_size_bytes, status)
                    VALUES (@TenantId, @SiteId, @actor, @name, 'file_upload', @size, 'created')
                    RETURNING {Columns}
                    """,
                    new { tx.TenantId, body.SiteId, actor = access.Actor.ActorId, name, size });

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.create", "import_file", file!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.Created($"/api/v1/imports/{file.Id}", file);
            },
            cancellationToken);
    }

    private static async Task<IResult> ListAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        return Results.Ok(await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<ImportFileResponse>(
                $"SELECT {Columns} FROM hub.import_files WHERE (@all OR site_id = ANY(@allowed)) AND NOT site_id = ANY(@denied) ORDER BY created_at DESC, id LIMIT 200",
                new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() }),
            cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var file = await FindAsync(database, access.Actor.TenantId, id, cancellationToken);
        return file is null || !InScope(http, file) ? Problems.NotFound() : Results.Ok(file);
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
                var updated = await tx.QuerySingleOrDefaultAsync<ImportFileResponse>(
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

        return Results.Ok(file);
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
                var updated = await tx.QuerySingleOrDefaultAsync<ImportFileResponse>(
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

        return file is null ? Problems.Conflict("O envio deste arquivo já foi concluído.") : Results.Ok(file);
    }

    private static async Task<Guid?> BlobOfAsync(ITenantDatabase database, Guid tenant, Guid id, CancellationToken cancellationToken)
    {
        var text = await database.InTenantAsync(tenant, tx => tx.QuerySingleOrDefaultAsync<string?>("SELECT encrypted_blob_ref FROM hub.import_files WHERE id = @id", new { id }), cancellationToken);
        return Guid.TryParse(text, out var blob) ? blob : null;
    }

    private static Task<ImportFileResponse?> FindAsync(ITenantDatabase database, Guid tenant, Guid id, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenant, tx => tx.QuerySingleOrDefaultAsync<ImportFileResponse>($"SELECT {Columns} FROM hub.import_files WHERE id = @id", new { id }), cancellationToken);

    private static bool InScope(HttpContext http, ImportFileResponse file) =>
        file.SiteId is { } site && http.GetScope().Includes(site);

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
