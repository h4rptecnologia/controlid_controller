using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Storage;

namespace Platform.Modules.Importation;

public sealed class PreviewLimits
{
    /// <summary>Pessoas por execucao: a conciliacao mantem todas em memoria.</summary>
    public int MaxPeople { get; init; } = 200_000;

    /// <summary>Linhas invalidas de outros objetos guardadas para consulta. Acima disso, so a contagem.</summary>
    public int MaxStoredInvalidRows { get; init; } = 10_000;

    public TimeSpan PreviewValidity { get; init; } = TimeSpan.FromHours(24);
}

public static class RunErrors
{
    public const string OriginalUnavailable = "original_unavailable";
    public const string PreviewFailed = "preview_failed";
    public const string TooManyPeople = "too_many_people";
    public const string UnreadableContent = "unreadable_content";
    public const string LayoutChanged = "layout_not_recognized";
}

public sealed record PreviewEventCounts(long Total, long AlreadyImported, long New, long NotCompared);

public sealed record PreviewCredentialCounts(long New, long SamePerson, long OtherPerson);

/// <summary>Totais da previa, sem nenhum valor do arquivo. PIN nao tem indice de busca: fica sem comparacao, e a tela diz isso.</summary>
public sealed record PreviewStats(
    IReadOnlyDictionary<string, long> People,
    IReadOnlyDictionary<string, PreviewCredentialCounts> Credentials,
    IReadOnlyDictionary<string, long> CredentialsNotCompared,
    long BiometricsExcluded,
    PreviewEventCounts Events,
    IReadOnlyDictionary<string, long> OtherObjects,
    IReadOnlyDictionary<string, long> PendingReferences,
    long InvalidOtherRows,
    long InvalidRowsNotStored,
    IReadOnlyList<string> SensitiveColumnsIgnored,
    int UnknownSections,
    string? ParserId);

/// <summary>
/// Gera a previa no Worker. So le o cadastro: nada aqui grava em pessoas nem fala com equipamento. Linhas e estado entram
/// numa unica transacao, para uma execucao interrompida poder recomecar do zero sem meias previas.
/// </summary>
public sealed class ImportPreviewService(
    NpgsqlDataSource dataSource,
    ITenantDatabase database,
    EncryptedBlobStore store,
    CredentialProtector protector,
    PreviewLimits limits,
    ILogger<ImportPreviewService> logger)
{
    private const int MaxAttempts = 3;
    private const int MemoryThreshold = 64 * 1024 * 1024;
    private const int InsertBatch = 1000;
    private const string EventFamily = "access_logs";
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["cards"] = CredentialKinds.Card,
        ["qrcodes"] = CredentialKinds.Qr,
        ["uhf_tags"] = CredentialKinds.Uhf,
        ["pins"] = CredentialKinds.Pin,
    };
    private static readonly HashSet<string> Biometrics = new(StringComparer.Ordinal) { "templates" };

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        ClaimedRun? claimed;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            claimed = await connection.QuerySingleOrDefaultAsync<ClaimedRun>(
                "SELECT tenant_id AS TenantId, run_id AS RunId, file_id AS FileId, attempts AS Attempts FROM hub.claim_next_import_run(@stale)",
                new { stale = Stale });
        }

        if (claimed is null)
        {
            return false;
        }

        if (claimed.Attempts > MaxAttempts)
        {
            await FailAsync(claimed, RunErrors.PreviewFailed, cancellationToken);
            return true;
        }

        try
        {
            await BuildAsync(claimed, cancellationToken);
        }
        catch (PreviewFailure failure)
        {
            await FailAsync(claimed, failure.Code, cancellationToken);
        }
        catch (Exception ex) when (ex is KeyServiceException or IOException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonClientException or HttpRequestException)
        {
            logger.LogWarning("Previa adiada por indisponibilidade: {Kind}.", ex.GetType().Name);
            await database.InTenantAsync(
                claimed.TenantId,
                tx => tx.ExecuteAsync("UPDATE hub.import_runs SET status = 'queued', started_at = NULL WHERE id = @RunId AND status = 'processing'", new { claimed.RunId }),
                cancellationToken);
        }
        catch (BlobIntegrityException)
        {
            await FailAsync(claimed, RunErrors.OriginalUnavailable, cancellationToken);
        }

        return true;
    }

    private async Task BuildAsync(ClaimedRun claimed, CancellationToken cancellationToken)
    {
        var job = await database.InTenantAsync(
            claimed.TenantId,
            tx => tx.QuerySingleOrDefaultAsync<JobRow>(
                """
                SELECT f.encrypted_blob_ref AS BlobRef, f.content_size_bytes AS Size, f.detected_type AS DetectedType, f.site_id AS SiteId, f.source_format AS SourceFormat,
                       r.device_id AS DeviceId, r.mapping::text AS MappingText, r.field_policy::text AS PolicyText, r.source_system AS SourceSystem
                FROM hub.import_runs r JOIN hub.import_files f ON f.tenant_id = r.tenant_id AND f.id = r.file_id
                WHERE r.id = @RunId
                """,
                new { claimed.RunId }),
            cancellationToken);

        if (job?.BlobRef is null || !Guid.TryParse(job.BlobRef, out var blobId) || job.DetectedType is null)
        {
            throw new PreviewFailure(RunErrors.OriginalUnavailable);
        }

        await using Stream plain = (job.Size ?? 0) <= MemoryThreshold
            ? new MemoryStream()
            : new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        if (!await store.ReadAsync(claimed.TenantId, blobId, plain, cancellationToken))
        {
            throw new PreviewFailure(RunErrors.OriginalUnavailable);
        }

        var mapping = job.MappingText is null ? null : JsonSerializer.Deserialize<TabularMapping>(job.MappingText, Json);
        var policy = JsonSerializer.Deserialize<Dictionary<string, string>>(job.PolicyText ?? "{}", Json) ?? [];

        using var csv = ImportCsvSource.For(plain, job.DetectedType, cancellationToken) ?? throw new PreviewFailure(RunErrors.LayoutChanged);

        var collected = mapping is not null
            ? await ReadMappedAsync(csv, mapping, cancellationToken)
            : await ReadNativeAsync(csv, job.SourceFormat, cancellationToken);

        var existing = await LoadExistingAsync(claimed.TenantId, job, collected.People, cancellationToken);
        var rows = PreviewClassifier.Classify(collected.People, existing, policy);
        var events = await CountEventsAsync(claimed.TenantId, job.DeviceId, collected.EventIds, cancellationToken);
        var (credentialCounts, credentialConflicts) = await CompareCredentialsAsync(claimed.TenantId, collected, rows, cancellationToken);

        var people = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        if (credentialConflicts.Count > 0)
        {
            people[PreviewKinds.FieldConflict] = people.GetValueOrDefault(PreviewKinds.FieldConflict) + credentialConflicts.Count;
        }

        var stats = new PreviewStats(
            people,
            credentialCounts,
            collected.CredentialsNotCompared,
            collected.Biometrics,
            events,
            collected.Others,
            collected.Summary?.PendingReferences ?? new Dictionary<string, long>(),
            collected.InvalidOther.Count + collected.InvalidNotStored,
            collected.InvalidNotStored,
            collected.Summary?.Objects.SelectMany(o => o.SensitiveColumns.Concat(o.DiscardedColumns).Select(c => $"{o.Name}.{c}")).ToList() ?? [],
            collected.Summary?.UnknownSections ?? 0,
            collected.Summary?.ParserId);

        await PersistAsync(claimed, rows, credentialConflicts, collected.InvalidOther, stats, cancellationToken);
    }

    private async Task<Collected> ReadNativeAsync(ImportCsvSource csv, string? format, CancellationToken cancellationToken)
    {
        var (encoding, _) = await DetectEncodingAsync(csv, cancellationToken);
        var csvLimits = new CsvLimits();
        Func<Task<TextReader>> open = async () => new StreamReader(await csv.OpenAsync(), encoding, detectEncodingFromByteOrderMarks: false);

        var layout = await NativeCsvParser.DetectLayoutAsync(open, SourceAnalyzer.FlatObjectOf(format ?? SourceFormats.Tabular), csvLimits, cancellationToken)
            ?? throw new PreviewFailure(RunErrors.LayoutChanged);

        var collected = new Collected();
        using var reader = await open();
        try
        {
            collected.Summary = await NativeCsvParser.ParseAsync(
                reader,
                layout,
                csvLimits,
                (record, _) =>
                {
                    Collect(collected, record);
                    return ValueTask.CompletedTask;
                },
                cancellationToken);
        }
        catch (CsvFormatException)
        {
            throw new PreviewFailure(RunErrors.UnreadableContent);
        }

        return collected;
    }

    private void Collect(Collected collected, NativeRecord record)
    {
        if (record.ObjectName == "users")
        {
            if (collected.People.Count >= limits.MaxPeople)
            {
                throw new PreviewFailure(RunErrors.TooManyPeople);
            }

            collected.People.Add(PersonReader.FromNative(record));
            return;
        }

        if (Credentials.TryGetValue(record.ObjectName, out var kind))
        {
            CollectCredential(collected, record, kind);
        }
        else if (Biometrics.Contains(record.ObjectName))
        {
            collected.Biometrics++;
        }
        else if (record.ObjectName == "access_logs")
        {
            if (record.Errors.Count == 0 && record.Fields.TryGetValue("id", out var id) && PreviewClassifier.IsSafeKey(id))
            {
                collected.EventIds.Add(id);
            }
        }
        else
        {
            collected.Others[record.ObjectName] = collected.Others.GetValueOrDefault(record.ObjectName) + 1;
        }

        if (record.Errors.Count > 0)
        {
            if (collected.InvalidOther.Count < limits.MaxStoredInvalidRows)
            {
                // A chave so entra se for numero; o valor da celula, nunca.
                var key = record.Fields.GetValueOrDefault("id");
                collected.InvalidOther.Add(new InvalidRow(record.RowNumber, record.ObjectName, PreviewClassifier.IsSafeKey(key) ? key : null, record.Errors));
            }
            else
            {
                collected.InvalidNotStored++;
            }
        }
    }

    private void CollectCredential(Collected collected, NativeRecord record, string kind)
    {
        // PIN nao e comparado: nao tem indice de busca, e o valor nem fica em memoria alem desta linha.
        if (!CredentialKinds.IsSearchable(kind))
        {
            collected.CredentialsNotCompared[record.ObjectName] = collected.CredentialsNotCompared.GetValueOrDefault(record.ObjectName) + 1;
            return;
        }

        if (record.Errors.Count > 0 || !record.Fields.TryGetValue("value", out var value))
        {
            return;
        }

        if (!CredentialValue.TryNormalize(kind, value, out var normalized))
        {
            collected.CredentialsInvalid.Add(new InvalidRow(record.RowNumber, record.ObjectName, null, [PreviewCodes.InvalidCredential]));
            return;
        }

        if (collected.CredentialValues.Count >= limits.MaxPeople)
        {
            throw new PreviewFailure(RunErrors.TooManyPeople);
        }

        collected.CredentialValues.Add(new CredentialCandidate(record.RowNumber, record.ObjectName, kind, record.Fields.GetValueOrDefault("user_id"), normalized));
    }

    /// <summary>
    /// Compara pelo indice cego, sem decifrar nada. Cartao que ja e de outra pessoa nunca passa sem decisao, e so pode ficar
    /// de fora: tirar credencial de alguem por importacao seria tomar o acesso dessa pessoa em silencio.
    /// </summary>
    private async Task<(Dictionary<string, PreviewCredentialCounts> Counts, List<StoredRow> Conflicts)> CompareCredentialsAsync(Guid tenant, Collected collected, IReadOnlyList<PreviewRow> rows, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, PreviewCredentialCounts>(StringComparer.Ordinal);
        var conflicts = new List<StoredRow>();
        var matchedBySource = rows.Where(r => r.SourceKey is not null && r.MatchPersonId is not null)
            .GroupBy(r => r.SourceKey!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().MatchPersonId!.Value, StringComparer.Ordinal);

        foreach (var group in collected.CredentialValues.GroupBy(c => c.ObjectName, StringComparer.Ordinal))
        {
            var items = group.ToList();
            var kind = items[0].Kind;
            var indexes = await protector.IndexManyAsync(tenant, kind, items.Select(i => i.Value).ToList(), cancellationToken);
            var hex = indexes.Select(Convert.ToHexStringLower).ToArray();
            var owners = (await database.InTenantAsync(
                tenant,
                tx => tx.QueryAsync<CredentialOwner>(
                    """
                    SELECT encode(blind_index, 'hex') AS IndexHex, person_id AS PersonId FROM hub.credentials
                    WHERE kind = @kind AND revoked_at IS NULL AND blind_index = ANY(SELECT decode(v, 'hex') FROM unnest(@hex::text[]) AS v)
                    """,
                    new { kind, hex }),
                cancellationToken)).ToDictionary(o => o.IndexHex, o => o.PersonId, StringComparer.Ordinal);

            long added = 0, same = 0, other = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (!owners.TryGetValue(hex[i], out var owner))
                {
                    added++;
                }
                else if (items[i].UserSourceId is { } user && matchedBySource.TryGetValue(user, out var match) && match == owner)
                {
                    same++;
                }
                else
                {
                    other++;
                    var sample = new PreviewSample(string.Empty, CredentialValue.Hint(kind, items[i].Value), [], []);
                    conflicts.Add(new StoredRow(items[i].Row, items[i].ObjectName, null, PreviewKinds.FieldConflict, null, [PreviewCodes.CredentialOfOtherPerson], JsonSerializer.Serialize(sample, Json)));
                }
            }

            counts[group.Key] = new PreviewCredentialCounts(added, same, other);
        }

        return (counts, conflicts);
    }

    private async Task<Collected> ReadMappedAsync(ImportCsvSource csv, TabularMapping mapping, CancellationToken cancellationToken)
    {
        var (encoding, _) = await DetectEncodingAsync(csv, cancellationToken);
        var delimiter = TabularMappingRules.DelimiterOf(mapping.Delimiter) ?? throw new PreviewFailure(RunErrors.LayoutChanged);
        var collected = new Collected();
        using var reader = new StreamReader(await csv.OpenAsync(), encoding, detectEncodingFromByteOrderMarks: false);
        var rowNumber = 0L;
        try
        {
            await foreach (var row in SafeCsvReader.ReadRowsAsync(reader, delimiter, new CsvLimits(), cancellationToken))
            {
                rowNumber++;
                if (rowNumber == 1 && mapping.HasHeader)
                {
                    continue;
                }

                if (collected.People.Count >= limits.MaxPeople)
                {
                    throw new PreviewFailure(RunErrors.TooManyPeople);
                }

                collected.People.Add(PersonReader.FromMapped(row, rowNumber, mapping));
            }
        }
        catch (CsvFormatException)
        {
            throw new PreviewFailure(RunErrors.UnreadableContent);
        }

        return collected;
    }

    private static async Task<(Encoding Encoding, string Name)> DetectEncodingAsync(ImportCsvSource csv, CancellationToken cancellationToken)
    {
        await using var stream = await csv.OpenAsync();
        try
        {
            return await SafeCsvReader.DetectEncodingAsync(stream, cancellationToken);
        }
        catch (CsvFormatException)
        {
            throw new PreviewFailure(RunErrors.UnreadableContent);
        }
    }

    /// <summary>So o que pode bater: por vinculo com o equipamento, matricula ou nome. O RLS limita ao tenant.</summary>
    private Task<IReadOnlyCollection<ExistingPerson>> LoadExistingAsync(Guid tenant, JobRow job, List<PersonCandidate> people, CancellationToken cancellationToken)
    {
        var registrations = people.Where(p => !string.IsNullOrEmpty(p.Registration)).Select(p => p.Registration!).Distinct(StringComparer.Ordinal).ToArray();
        var names = people.Where(p => !string.IsNullOrWhiteSpace(p.DisplayName))
            .Select(p => string.Join(' ', p.DisplayName!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
        var sources = job.DeviceId is null ? [] : people.Where(p => PreviewClassifier.IsSafeKey(p.SourceId)).Select(p => p.SourceId!).Distinct(StringComparer.Ordinal).ToArray();
        var externals = job.SourceSystem is null ? [] : people.Where(p => !string.IsNullOrEmpty(p.SourceId)).Select(p => p.SourceId!).Distinct(StringComparer.Ordinal).ToArray();

        return database.InTenantAsync<IReadOnlyCollection<ExistingPerson>>(
            tenant,
            async tx =>
            {
                var rows = await tx.QueryAsync<PersonRow>(
                    """
                    SELECT p.id AS Id, (p.site_id IS NULL OR p.site_id = @site) AS SameSite, p.display_name AS DisplayName, p.registration AS Registration,
                           p.person_type AS PersonType, p.valid_from AS ValidFrom, p.valid_to AS ValidTo, p.state AS State, l.source_user_id::text AS LinkedSourceId, k.external_key AS ExternalKey
                    FROM hub.people p
                    LEFT JOIN hub.person_device_links l ON l.tenant_id = p.tenant_id AND l.person_id = p.id AND l.device_id = @device
                    LEFT JOIN hub.entity_source_keys k ON k.tenant_id = p.tenant_id AND k.entity_type = 'person' AND k.entity_id = p.id AND k.source_system = @system
                    WHERE p.deleted_at IS NULL
                      AND (p.registration = ANY(@registrations)
                        OR lower(regexp_replace(trim(p.display_name), '\s+', ' ', 'g')) = ANY(@names)
                        OR l.source_user_id::text = ANY(@sources)
                        OR k.external_key = ANY(@externals))
                    """,
                    new { site = job.SiteId, device = job.DeviceId, system = job.SourceSystem, registrations, names, sources, externals });
                return rows.Select(r => new ExistingPerson(r.Id, r.SameSite, r.DisplayName, r.Registration, r.PersonType, ToOffset(r.ValidFrom), ToOffset(r.ValidTo), r.State, r.LinkedSourceId, r.ExternalKey)).ToList();
            },
            cancellationToken);
    }

    /// <summary>Sem equipamento informado nao ha como saber se o evento ja chegou: conta como nao comparado, nao como novo.</summary>
    private async Task<PreviewEventCounts> CountEventsAsync(Guid tenant, Guid? device, HashSet<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new PreviewEventCounts(0, 0, 0, 0);
        }

        if (device is null)
        {
            return new PreviewEventCounts(ids.Count, 0, 0, ids.Count);
        }

        var already = await database.InTenantAsync(
            tenant,
            tx => tx.QuerySingleOrDefaultAsync<long>(
                """
                SELECT count(DISTINCT e.source_event_id) FROM hub.access_events e
                JOIN hub.devices d ON d.tenant_id = e.tenant_id AND d.id = e.device_id
                WHERE e.device_id = @device AND e.source_family = @family AND e.source_epoch = d.source_epoch AND e.source_event_id = ANY(@ids)
                """,
                new { device, family = EventFamily, ids = ids.ToArray() }),
            cancellationToken);
        return new PreviewEventCounts(ids.Count, already, ids.Count - already, 0);
    }

    private async Task PersistAsync(ClaimedRun claimed, IReadOnlyList<PreviewRow> rows, List<StoredRow> credentialConflicts, List<InvalidRow> invalid, PreviewStats stats, CancellationToken cancellationToken)
    {
        // Chave descartada ao fim: dado de pouca entropia, como matricula, nao pode ser recuperado do digest por tentativa.
        var digestKey = RandomNumberGenerator.GetBytes(32);
        var payload = rows.Select(r => new StoredRow(r.Row, "users", r.SourceKey, r.Kind, r.MatchPersonId, r.Codes, JsonSerializer.Serialize(r.Sample, Json)))
            .Concat(credentialConflicts)
            .Concat(invalid.Select(r => new StoredRow(r.Row, r.ObjectName, r.Key, PreviewKinds.Invalid, null, r.Codes, "{}")))
            .ToList();

        await database.InTenantAsync(
            claimed.TenantId,
            async tx =>
            {
                var updated = await tx.ExecuteAsync(
                    """
                    UPDATE hub.import_runs
                    SET status = 'preview_ready', stats = @stats::jsonb, parser_id = @parser, expires_at = now() + @validity, error_code = NULL
                    WHERE id = @RunId AND status = 'processing'
                    """,
                    new { stats = JsonSerializer.Serialize(stats, Json), parser = stats.ParserId, validity = limits.PreviewValidity, claimed.RunId });

                // Cancelada enquanto processava: nada do que foi lido fica.
                if (updated == 0)
                {
                    return false;
                }

                foreach (var batch in payload.Chunk(InsertBatch))
                {
                    await tx.ExecuteAsync(
                        """
                        INSERT INTO hub.import_rows (tenant_id, run_id, line_no, source_object, source_key, row_digest, result_kind, match_person_id, sanitized_preview, error_codes)
                        SELECT @TenantId, @RunId, line, object, key, decode(digest, 'hex'), kind, person, sample::jsonb, string_to_array(codes, ',')
                        FROM unnest(@lines::bigint[], @objects::text[], @keys::text[], @digests::text[], @kinds::text[], @persons::uuid[], @samples::text[], @codes::text[])
                          AS t(line, object, key, digest, kind, person, sample, codes)
                        """,
                        new
                        {
                            tx.TenantId,
                            claimed.RunId,
                            lines = batch.Select(r => r.Row).ToArray(),
                            objects = batch.Select(r => r.ObjectName).ToArray(),
                            keys = batch.Select(r => r.Key).ToArray(),
                            digests = batch.Select(r => Convert.ToHexStringLower(HMACSHA256.HashData(digestKey, Encoding.UTF8.GetBytes($"{r.Row}|{r.ObjectName}|{r.Kind}|{r.Key}|{r.SampleJson}")))).ToArray(),
                            kinds = batch.Select(r => r.Kind).ToArray(),
                            persons = batch.Select(r => r.Person).ToArray(),
                            samples = batch.Select(r => r.SampleJson).ToArray(),
                            codes = batch.Select(r => string.Join(',', r.Codes)).ToArray(),
                        });
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(null, "import.preview_ready", "import_run", claimed.RunId.ToString(), AuditWriter.Success, null));
                return true;
            },
            cancellationToken);
    }

    private Task<bool> FailAsync(ClaimedRun claimed, string code, CancellationToken cancellationToken) =>
        database.InTenantAsync(
            claimed.TenantId,
            async tx =>
            {
                var updated = await tx.ExecuteAsync(
                    "UPDATE hub.import_runs SET status = 'failed', error_code = @code, completed_at = now() WHERE id = @RunId AND status = 'processing'",
                    new { code, claimed.RunId });
                if (updated > 0)
                {
                    await AuditWriter.WriteAsync(tx, new AuditEntry(null, "import.preview_failed", "import_run", claimed.RunId.ToString(), AuditWriter.Denied, null, Metadata: new { code }));
                }

                return updated > 0;
            },
            cancellationToken);

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed class Collected
    {
        public List<PersonCandidate> People { get; } = [];

        public List<CredentialCandidate> CredentialValues { get; } = [];

        public Dictionary<string, long> CredentialsNotCompared { get; } = new(StringComparer.Ordinal);

        public List<InvalidRow> CredentialsInvalid => InvalidOther;

        public Dictionary<string, long> Others { get; } = new(StringComparer.Ordinal);

        public HashSet<string> EventIds { get; } = new(StringComparer.Ordinal);

        public List<InvalidRow> InvalidOther { get; } = [];

        public long InvalidNotStored { get; set; }

        public long Biometrics { get; set; }

        public NativeParseSummary? Summary { get; set; }
    }

    private sealed record CredentialCandidate(long Row, string ObjectName, string Kind, string? UserSourceId, string Value);

    private sealed record CredentialOwner(string IndexHex, Guid PersonId);

    private sealed record InvalidRow(long Row, string ObjectName, string? Key, IReadOnlyList<string> Codes);

    private sealed record StoredRow(long Row, string ObjectName, string? Key, string Kind, Guid? Person, IReadOnlyList<string> Codes, string SampleJson);

    private sealed record ClaimedRun(Guid TenantId, Guid RunId, Guid FileId, int Attempts);

    private sealed record JobRow(string? BlobRef, long? Size, string? DetectedType, Guid? SiteId, string? SourceFormat, Guid? DeviceId, string? MappingText, string? PolicyText, string? SourceSystem);

    private sealed record PersonRow(Guid Id, bool SameSite, string DisplayName, string? Registration, string PersonType, DateTime? ValidFrom, DateTime? ValidTo, string State, string? LinkedSourceId, string? ExternalKey);

    private sealed class PreviewFailure : Exception
    {
        public PreviewFailure()
        {
        }

        public PreviewFailure(string code)
            : base(code)
        {
            Code = code;
        }

        public PreviewFailure(string code, Exception innerException)
            : base(code, innerException)
        {
            Code = code;
        }

        public string Code { get; } = RunErrors.PreviewFailed;
    }
}
