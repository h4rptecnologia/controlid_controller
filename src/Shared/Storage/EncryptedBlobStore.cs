using System.Security.Cryptography;
using Platform.Shared.Kernel.Data;

namespace Platform.Shared.Storage;

public enum BlobKind
{
    ImportOriginal,
    ImportPackage,
    Export,
    RawEvent,
    EventPhoto,
    PersonPhoto,
    BiometricTemplate,
    DeviceSecret,
}

public enum BlobClassification
{
    Internal,
    Personal,
    Sensitive,
    Secret,
}

public sealed record BlobReference(Guid Id, byte[] PlaintextSha256, long PlaintextSize);

public sealed record RewrapResult(int Rewrapped, long Remaining);

/// <summary>
/// Envelope encryption (docs/06-seguranca.md): DEK aleatoria por blob, embrulhada no KMS, com o cifrado no
/// object storage e os metadados no PostgreSQL. Texto claro nunca toca o disco nem o object storage.
/// </summary>
public sealed class EncryptedBlobStore(IObjectStore objects, IKeyService keys, ITenantDatabase database)
{
    public static string KeyName(Guid tenantId, KeyPurpose purpose) =>
        $"hub-{tenantId:N}-{purpose.ToString().ToLowerInvariant()}";

    /// <summary>A finalidade da chave decorre do tipo do blob; quem chama nao escolhe.</summary>
    public static KeyPurpose PurposeOf(BlobKind kind) => kind switch
    {
        BlobKind.BiometricTemplate => KeyPurpose.Biometric,
        BlobKind.DeviceSecret => KeyPurpose.Secret,
        _ => KeyPurpose.General,
    };

    public async Task<BlobReference> StoreAsync(
        Guid tenantId,
        BlobKind kind,
        BlobClassification classification,
        Stream plaintext,
        Guid? createdBy,
        DateTimeOffset? retainUntil,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var id = Guid.NewGuid();
        var kindName = KindName(kind);
        var classificationName = classification.ToString().ToLowerInvariant();
        var objectKey = $"tenants/{tenantId:N}/{kindName}/{id:N}";
        var keyName = KeyName(tenantId, PurposeOf(kind));

        var dek = RandomNumberGenerator.GetBytes(BlobCipher.DekSize);
        try
        {
            var wrapped = await keys.WrapAsync(keyName, dek, cancellationToken);

            await using var ciphertext = NewCiphertextBuffer();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var hashing = new HashingReadStream(plaintext, hash);
            await BlobCipher.EncryptAsync(hashing, ciphertext, dek, BlobCipher.BuildContext(tenantId, id, kindName, classificationName), cancellationToken);
            var digest = hash.GetHashAndReset();
            var ciphertextSize = ciphertext.Length;

            ciphertext.Position = 0;
            await objects.PutAsync(objectKey, ciphertext, cancellationToken);

            try
            {
                await database.InTenantAsync(
                    tenantId,
                    tx => tx.ExecuteAsync(
                        """
                        INSERT INTO hub.blob_objects
                          (id, tenant_id, kind, classification, object_key, key_name, wrapped_dek, format_version,
                           plaintext_sha256, plaintext_size, ciphertext_size, created_by, retain_until)
                        VALUES (@id, @TenantId, @kindName, @classificationName, @objectKey, @keyName, @wrapped, @version,
                                @digest, @plaintextSize, @ciphertextSize, @createdBy, @retainUntil)
                        """,
                        new
                        {
                            id,
                            tx.TenantId,
                            kindName,
                            classificationName,
                            objectKey,
                            keyName,
                            wrapped,
                            version = (int)BlobCipher.FormatVersion,
                            digest,
                            plaintextSize = hashing.BytesRead,
                            ciphertextSize,
                            createdBy,
                            retainUntil,
                        }),
                    cancellationToken);
            }
            catch
            {
                // Sem linha, o objeto ficaria orfao e sem DEK recuperavel pela aplicacao.
                await objects.DeleteAsync(objectKey, CancellationToken.None);
                throw;
            }

            return new BlobReference(id, digest, hashing.BytesRead);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Lanca BlobIntegrityException se houver adulteracao; nesse caso o destino deve ser descartado.</summary>
    public async Task<bool> ReadAsync(Guid tenantId, Guid blobId, Stream destination, CancellationToken cancellationToken)
    {
        var row = await database.InTenantAsync(
            tenantId,
            tx => tx.QuerySingleOrDefaultAsync<BlobRow>(
                """
                SELECT kind AS Kind, classification AS Classification, object_key AS ObjectKey, key_name AS KeyName, wrapped_dek AS WrappedDek
                FROM hub.blob_objects WHERE id = @blobId AND destroyed_at IS NULL
                """,
                new { blobId }),
            cancellationToken);

        if (row is null)
        {
            return false;
        }

        var dek = await keys.UnwrapAsync(row.KeyName, row.WrappedDek, cancellationToken);
        try
        {
            await using var ciphertext = NewCiphertextBuffer();
            await objects.GetAsync(row.ObjectKey, ciphertext, cancellationToken);
            ciphertext.Position = 0;
            await BlobCipher.DecryptAsync(ciphertext, destination, dek, BlobCipher.BuildContext(tenantId, blobId, row.Kind, row.Classification), cancellationToken);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Elimina a DEK primeiro: copias em versoes e replicas do objeto deixam de ser legiveis.</summary>
    public async Task<bool> DestroyAsync(Guid tenantId, Guid blobId, CancellationToken cancellationToken)
    {
        var objectKey = await database.InTenantAsync(
            tenantId,
            tx => tx.QuerySingleOrDefaultAsync<string>(
                "UPDATE hub.blob_objects SET wrapped_dek = NULL, destroyed_at = now() WHERE id = @blobId AND destroyed_at IS NULL RETURNING object_key",
                new { blobId }),
            cancellationToken);

        if (objectKey is null)
        {
            return false;
        }

        await objects.DeleteAsync(objectKey, cancellationToken);
        return true;
    }

    public async Task RotateKeyAsync(Guid tenantId, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        // O transit so cria a chave no primeiro uso; sem isto, girar chave nunca usada falharia.
        var keyName = KeyName(tenantId, purpose);
        await keys.WrapAsync(keyName, new byte[BlobCipher.DekSize], cancellationToken);
        await keys.RotateAsync(keyName, cancellationToken);
    }

    /// <summary>Re-embrulha um lote. Repetir ate Remaining = 0 antes de aposentar a versao antiga da KEK.</summary>
    public async Task<RewrapResult> RewrapBatchAsync(Guid tenantId, KeyPurpose purpose, int batchSize, CancellationToken cancellationToken)
    {
        var keyName = KeyName(tenantId, purpose);
        var currentPrefix = await CurrentVersionPrefixAsync(keyName, cancellationToken);

        var rows = await database.InTenantAsync(
            tenantId,
            tx => tx.QueryAsync<RewrapRow>(
                """
                SELECT id AS Id, wrapped_dek AS WrappedDek FROM hub.blob_objects
                WHERE key_name = @keyName AND destroyed_at IS NULL AND wrapped_dek NOT LIKE @prefix
                ORDER BY id LIMIT @batchSize
                """,
                new { keyName, prefix = currentPrefix + "%", batchSize }),
            cancellationToken);

        var rewrapped = 0;
        foreach (var row in rows)
        {
            var updated = await keys.RewrapAsync(keyName, row.WrappedDek, cancellationToken);

            // Condicional: uma destruicao concorrente nao pode ter a DEK ressuscitada.
            rewrapped += await database.InTenantAsync(
                tenantId,
                tx => tx.ExecuteAsync(
                    "UPDATE hub.blob_objects SET wrapped_dek = @updated WHERE id = @Id AND wrapped_dek = @WrappedDek",
                    new { updated, row.Id, row.WrappedDek }),
                cancellationToken);
        }

        var remaining = await database.InTenantAsync(
            tenantId,
            tx => tx.QuerySingleOrDefaultAsync<long>(
                "SELECT count(*) FROM hub.blob_objects WHERE key_name = @keyName AND destroyed_at IS NULL AND wrapped_dek NOT LIKE @prefix",
                new { keyName, prefix = currentPrefix + "%" }),
            cancellationToken);

        return new RewrapResult(rewrapped, remaining);
    }

    // O transit identifica a versao no prefixo do cifrado ("vault:v3:"). Embrulhar um valor descartavel revela a atual.
    private async Task<string> CurrentVersionPrefixAsync(string keyName, CancellationToken cancellationToken)
    {
        var probe = await keys.WrapAsync(keyName, new byte[BlobCipher.DekSize], cancellationToken);
        var end = probe.IndexOf(':', probe.IndexOf(':', StringComparison.Ordinal) + 1);
        return end > 0 ? probe[..(end + 1)] : throw new KeyServiceException();
    }

    private static string KindName(BlobKind kind) => kind switch
    {
        BlobKind.ImportOriginal => "import_original",
        BlobKind.ImportPackage => "import_package",
        BlobKind.Export => "export",
        BlobKind.RawEvent => "raw_event",
        BlobKind.EventPhoto => "event_photo",
        BlobKind.PersonPhoto => "person_photo",
        BlobKind.BiometricTemplate => "biometric_template",
        BlobKind.DeviceSecret => "device_secret",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // Guarda so cifrado. Em disco acima de 8 MiB para nao segurar arquivos grandes em memoria.
    private static SpillBuffer NewCiphertextBuffer() => new(8 * 1024 * 1024);

    private sealed record BlobRow(string Kind, string Classification, string ObjectKey, string KeyName, string WrappedDek);

    private sealed record RewrapRow(Guid Id, string WrappedDek);
}
