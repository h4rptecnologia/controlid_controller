using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Storage;

namespace Platform.Modules.Importation;

/// <summary>
/// Processa a fila da quarentena no Worker, processo separado da API. Ordem: estrutura primeiro, porque nao adianta
/// enviar uma bomba ao antivirus. Recusado tem o conteudo destruido; a linha fica como trilha de custodia.
/// </summary>
public sealed class ImportInspectionService(
    NpgsqlDataSource dataSource,
    ITenantDatabase database,
    EncryptedBlobStore store,
    IMalwareScanner scanner,
    InspectionLimits limits,
    ILogger<ImportInspectionService> logger)
{
    private const int MaxAttempts = 3;
    private const int MemoryThreshold = 64 * 1024 * 1024;
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    private static readonly System.Text.Json.JsonSerializerOptions AnalysisJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Falso quando a fila esta vazia.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        ClaimedFile? claimed;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            claimed = await connection.QuerySingleOrDefaultAsync<ClaimedFile>(
                "SELECT tenant_id AS TenantId, file_id AS FileId, attempts AS Attempts FROM hub.claim_next_import(@stale)",
                new { stale = Stale });
        }

        if (claimed is null)
        {
            return false;
        }

        var file = await database.InTenantAsync(
            claimed.TenantId,
            tx => tx.QuerySingleOrDefaultAsync<FileRow>(
                "SELECT encrypted_blob_ref AS BlobRef, content_size_bytes AS Size, unlocked_from_package AS Unlocked FROM hub.import_files WHERE id = @FileId",
                new { claimed.FileId }),
            cancellationToken);

        if (file?.BlobRef is null || !Guid.TryParse(file.BlobRef, out var blobId))
        {
            await RejectAsync(claimed, null, RejectionCodes.InspectionFailed, cancellationToken);
            return true;
        }

        if (claimed.Attempts > MaxAttempts)
        {
            await RejectAsync(claimed, blobId, RejectionCodes.InspectionFailed, cancellationToken);
            return true;
        }

        try
        {
            await InspectAsync(claimed, blobId, file.Size ?? 0, file.Unlocked, cancellationToken);
        }
        catch (Exception ex) when (ex is KeyServiceException or IOException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonClientException or HttpRequestException)
        {
            // Infraestrutura fora: volta para a fila, e o limite de tentativas evita ficar preso para sempre.
            logger.LogWarning("Inspecao adiada por indisponibilidade: {Kind}.", ex.GetType().Name);
            await database.InTenantAsync(
                claimed.TenantId,
                tx => tx.ExecuteAsync("UPDATE hub.import_files SET status = 'quarantined', inspection_started_at = NULL WHERE id = @FileId AND status = 'inspecting'", new { claimed.FileId }),
                cancellationToken);
        }
        catch (BlobIntegrityException)
        {
            await RejectAsync(claimed, blobId, RejectionCodes.InspectionFailed, cancellationToken);
        }

        return true;
    }

    private async Task InspectAsync(ClaimedFile claimed, Guid blobId, long size, bool unlocked, CancellationToken cancellationToken)
    {
        // Precisa de acesso aleatorio. Acima do limite vai para arquivo temporario em claro, apagado ao fechar:
        // o diretorio temporario do Worker tem de ser privado e, de preferencia, em memoria (tmpfs).
        await using Stream plain = size <= MemoryThreshold
            ? new MemoryStream()
            : new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        if (!await store.ReadAsync(claimed.TenantId, blobId, plain, cancellationToken))
        {
            await RejectAsync(claimed, null, RejectionCodes.InspectionFailed, cancellationToken);
            return;
        }

        var inspection = await FileInspector.InspectAsync(plain, limits, cancellationToken);
        if (inspection.RejectionCode is { } code)
        {
            await RejectAsync(claimed, blobId, code, cancellationToken, inspection.DetectedType);
            return;
        }

        // Cifrado nao tem o que inspecionar: o conteudo volta para esta mesma fila depois de aberto.
        if (inspection.DetectedType == DetectedTypes.HubPackage)
        {
            if (unlocked)
            {
                await RejectAsync(claimed, blobId, RejectionCodes.NestedPackage, cancellationToken, inspection.DetectedType);
                return;
            }

            await database.InTenantAsync(
                claimed.TenantId,
                async tx =>
                {
                    await tx.ExecuteAsync("UPDATE hub.import_files SET status = 'awaiting_password', detected_type = @type, inspection_started_at = NULL WHERE id = @FileId", new { type = inspection.DetectedType, claimed.FileId });
                    await AuditWriter.WriteAsync(tx, new AuditEntry(null, "import.awaiting_password", "import_file", claimed.FileId.ToString(), AuditWriter.Success, null));
                    return true;
                },
                cancellationToken);
            return;
        }

        var scan = await scanner.ScanAsync(plain, cancellationToken);
        switch (scan.Verdict)
        {
            case ScanVerdict.Infected:
                await RejectAsync(claimed, blobId, RejectionCodes.Malware, cancellationToken, inspection.DetectedType, scan.Signature);
                return;
            case ScanVerdict.Unavailable:
                await database.InTenantAsync(
                    claimed.TenantId,
                    tx => tx.ExecuteAsync("UPDATE hub.import_files SET status = 'quarantined', scan_status = 'unavailable', inspection_started_at = NULL WHERE id = @FileId", new { claimed.FileId }),
                    cancellationToken);
                return;
        }

        var analysis = await SourceAnalyzer.AnalyzeAsync(plain, inspection.DetectedType!, new CsvLimits(), cancellationToken);

        await database.InTenantAsync(
            claimed.TenantId,
            async tx =>
            {
                await tx.ExecuteAsync(
                    """
                    UPDATE hub.import_files
                    SET status = 'inspected', detected_type = @DetectedType, entry_count = @EntryCount, uncompressed_bytes = @UncompressedBytes,
                        scan_status = 'clean', inspected_at = now(), source_format = @format, parser_status = @parser, analysis = @analysisJson::jsonb
                    WHERE id = @FileId
                    """,
                    new
                    {
                        inspection.DetectedType,
                        inspection.EntryCount,
                        inspection.UncompressedBytes,
                        format = analysis.SourceFormat,
                        parser = analysis.ParserStatus,
                        analysisJson = System.Text.Json.JsonSerializer.Serialize(analysis, AnalysisJson),
                        claimed.FileId,
                    });
                await AuditWriter.WriteAsync(tx, new AuditEntry(null, "import.inspected", "import_file", claimed.FileId.ToString(), AuditWriter.Success, null));
                return true;
            },
            cancellationToken);
    }

    private async Task RejectAsync(ClaimedFile claimed, Guid? blobId, string code, CancellationToken cancellationToken, string? detectedType = null, string? signature = null)
    {
        await database.InTenantAsync(
            claimed.TenantId,
            async tx =>
            {
                await tx.ExecuteAsync(
                    """
                    UPDATE hub.import_files
                    SET status = 'rejected', rejection_code = @code, detected_type = COALESCE(@detectedType, detected_type),
                        scan_status = CASE WHEN @code = @malware THEN 'infected' ELSE scan_status END, scan_signature = @signature, inspected_at = now()
                    WHERE id = @FileId
                    """,
                    new { code, detectedType, malware = RejectionCodes.Malware, signature, claimed.FileId });
                await AuditWriter.WriteAsync(tx, new AuditEntry(null, "import.rejected", "import_file", claimed.FileId.ToString(), AuditWriter.Denied, null, Metadata: new { code, signature }));
                return true;
            },
            cancellationToken);

        // Sem a chave, o conteudo perigoso deixa de existir mesmo em copias do objeto.
        if (blobId is { } id)
        {
            await store.DestroyAsync(claimed.TenantId, id, CancellationToken.None);
        }
    }

    private sealed record ClaimedFile(Guid TenantId, Guid FileId, int Attempts);

    private sealed record FileRow(string? BlobRef, long? Size, bool Unlocked);
}
