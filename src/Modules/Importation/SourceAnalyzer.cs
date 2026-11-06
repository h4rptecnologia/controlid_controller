using System.IO.Compression;

namespace Platform.Modules.Importation;

public static class SourceFormats
{
    public const string Users = "controlid_users_zip";
    public const string Sync = "controlid_sync_zip";
    public const string Backup = "controlid_backup_zip";
    public const string LogsZip = "controlid_logs_zip";
    public const string LogsCsv = "controlid_logs_csv";
    public const string ObjectsCsv = "controlid_objects_csv";
    public const string Tabular = "tabular";
    public const string UnknownArchive = "unknown_archive";
}

public static class ParserStatuses
{
    /// <summary>Lido pelos nomes da documentacao oficial. Nao significa validado contra arquivo real de equipamento.</summary>
    public const string ParsedByDocumentation = "parsed_by_documentation";

    /// <summary>Nao ha parser homologado para este arquivo. Exige mapeamento feito por uma pessoa.</summary>
    public const string NeedsManualMapping = "needs_manual_mapping";

    /// <summary>O CSV nao pode ser lido com seguranca; nem mapeamento manual resolveria.</summary>
    public const string Unreadable = "unreadable";
}

public static class AnalysisProblems
{
    /// <summary>O nome diz que e nativo, mas o conteudo nao bate com nenhum objeto documentado.</summary>
    public const string NativeLayoutNotRecognized = "native_layout_not_recognized";

    /// <summary>Os objetos encontrados contradizem o tipo de exportacao: por exemplo, logs dentro de sync.csv.</summary>
    public const string ObjectsContradictFormat = "objects_contradict_format";
}

/// <summary>
/// Somente contagens, mais nomes que constam da documentacao oficial. Coluna desconhecida fica de fora de proposito: sem
/// cabecalho, a primeira linha seria dado pessoal ou PIN num JSONB tecnico. Quem precisa dela le o original cifrado, com
/// permissao e auditoria.
/// </summary>
public sealed record SourceAnalysis(
    string SourceFormat,
    string ParserStatus,
    string? Encoding,
    string? Delimiter,
    int ColumnCount,
    long DataRows,
    long RowsWithDifferentWidth,
    long FormulaLikeCells,
    int ImageEntries,
    string? Problem,
    int InvalidImageEntries = 0,
    NativeParseSummary? Native = null);

/// <summary>
/// Identifica o que foi enviado. A documentacao oficial descreve a estrutura do ZIP e os campos de cada objeto, mas nao o
/// layout do CSV nem o que muda entre V5 e V6. O conteudo e lido pelos nomes oficiais; o que nao bate vai para mapeamento
/// manual, e a linha de firmware fica indeterminada. Nada aqui foi validado contra arquivo real de equipamento.
/// </summary>
public static class SourceAnalyzer
{
    private const string ImagesFolder = "user_images/";

    private static readonly Dictionary<string, string> NativeCsvNames = new(StringComparer.Ordinal)
    {
        ["users.csv"] = SourceFormats.Users,
        ["sync.csv"] = SourceFormats.Sync,
        ["backup.csv"] = SourceFormats.Backup,
        ["logs.csv"] = SourceFormats.LogsZip,
    };

    private static readonly HashSet<string> LogObjects = new(StringComparer.Ordinal) { "access_logs", "alarm_logs", "access_log_access_rules" };

    public static async Task<SourceAnalysis> AnalyzeAsync(Stream content, string detectedType, CsvLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return detectedType == DetectedTypes.Zip
            ? await AnalyzeZipAsync(content, limits, cancellationToken)
            : await AnalyzeCsvAsync(() => { content.Position = 0; return Task.FromResult(new NonDisposingStream(content) as Stream); }, SourceFormats.Tabular, 0, 0, limits, cancellationToken);
    }

    private static async Task<SourceAnalysis> AnalyzeZipAsync(Stream content, CsvLimits limits, CancellationToken cancellationToken)
    {
        content.Position = 0;
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        var images = files.Count(e => e.FullName.StartsWith(ImagesFolder, StringComparison.Ordinal));
        var others = files.Where(e => !e.FullName.StartsWith(ImagesFolder, StringComparison.Ordinal)).ToList();

        // Exatamente a estrutura documentada: um CSV conhecido na raiz, mais user_images. Qualquer outra coisa e desconhecida.
        if (others.Count == 1 && NativeCsvNames.TryGetValue(others[0].FullName, out var format))
        {
            var invalidImages = await CountInvalidImagesAsync(files, cancellationToken);
            return await AnalyzeCsvAsync(() => others[0].OpenAsync(cancellationToken), format, images, invalidImages, limits, cancellationToken);
        }

        return new SourceAnalysis(SourceFormats.UnknownArchive, ParserStatuses.NeedsManualMapping, null, null, 0, 0, 0, 0, images, null);
    }

    /// <summary>Confere a assinatura, sem decodificar a imagem. Conteudo corrompido conta como invalido.</summary>
    private static async Task<int> CountInvalidImagesAsync(List<ZipArchiveEntry> files, CancellationToken cancellationToken)
    {
        var invalid = 0;
        var head = new byte[8];
        foreach (var entry in files.Where(e => e.FullName.StartsWith(ImagesFolder, StringComparison.Ordinal)))
        {
            try
            {
                await using var stream = await entry.OpenAsync(cancellationToken);
                var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
                var jpeg = read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
                var png = read == 8 && head.AsSpan().SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
                if (!jpeg && !png)
                {
                    invalid++;
                }
            }
            catch (InvalidDataException)
            {
                invalid++;
            }
        }

        return invalid;
    }

    private static string? FlatObjectOf(string format) => format switch
    {
        SourceFormats.Users => "users",
        SourceFormats.LogsZip or SourceFormats.Tabular => "access_logs",
        _ => null,
    };

    /// <summary>
    /// A documentacao define o que cada exportacao carrega: so backup e logs tem logs de acesso. Os demais objetos nao sao
    /// restringidos, porque a pagina nao os enumera; o que for desconhecido e so contado.
    /// </summary>
    private static bool Contradicts(string format, NativeParseSummary native)
    {
        var names = native.Objects.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        return format switch
        {
            SourceFormats.Users => !names.Contains("users") || names.Overlaps(LogObjects),
            SourceFormats.Sync => names.Overlaps(LogObjects),
            SourceFormats.LogsZip or SourceFormats.LogsCsv => !names.Contains("access_logs") || names.Any(n => !LogObjects.Contains(n)),
            _ => false,
        };
    }

    private static async Task<SourceAnalysis> AnalyzeCsvAsync(Func<Task<Stream>> open, string format, int images, int invalidImages, CsvLimits limits, CancellationToken cancellationToken)
    {
        string? encodingName = null;
        string? delimiterName = null;
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(limits.MaxDuration);
        var deadline = deadlineSource.Token;
        try
        {
            System.Text.Encoding encoding;
            await using (var first = await open())
            {
                (encoding, encodingName) = await SafeCsvReader.DetectEncodingAsync(first, deadline);
            }

            Func<Task<TextReader>> openText = async () => new StreamReader(await open(), encoding, detectEncodingFromByteOrderMarks: false);
            var layout = await NativeCsvParser.DetectLayoutAsync(openText, FlatObjectOf(format), limits, deadline);

            // Sem ZIP, so conta como nativo o que nao aparece por acaso: as colunas de logs.csv ou secoes de objetos.
            if (layout is not null)
            {
                delimiterName = layout.Delimiter == '\t' ? "tab" : layout.Delimiter.ToString();
                using var text = await openText();
                var native = await NativeCsvParser.ParseAsync(text, layout, limits, null, deadline);
                var effective = format != SourceFormats.Tabular ? format : layout.Sectioned ? SourceFormats.ObjectsCsv : SourceFormats.LogsCsv;
                var contradicted = Contradicts(effective, native);
                return new SourceAnalysis(
                    effective,
                    contradicted ? ParserStatuses.NeedsManualMapping : ParserStatuses.ParsedByDocumentation,
                    encodingName,
                    delimiterName,
                    native.Objects.Sum(o => o.Columns.Count),
                    native.Objects.Sum(o => o.Rows),
                    native.Objects.Sum(o => o.Errors.GetValueOrDefault(NativeRowErrors.WidthMismatch)),
                    native.FormulaLikeCells,
                    images,
                    contradicted ? AnalysisProblems.ObjectsContradictFormat : null,
                    invalidImages,
                    native);
            }

            char delimiter;
            await using (var second = await open())
            using (var reader = new StreamReader(second, encoding, detectEncodingFromByteOrderMarks: false))
            {
                delimiter = await SafeCsvReader.DetectDelimiterAsync(reader, limits, deadline);
            }

            delimiterName = delimiter == '\t' ? "tab" : delimiter.ToString();

            await using var third = await open();
            using var rows = new StreamReader(third, encoding, detectEncodingFromByteOrderMarks: false);

            int? columns = null;
            var data = 0L;
            var different = 0L;
            var formulas = 0L;
            await foreach (var row in SafeCsvReader.ReadRowsAsync(rows, delimiter, limits, deadline))
            {
                if (columns is null)
                {
                    columns = row.Count;
                    continue;
                }

                data++;
                if (row.Count != columns)
                {
                    different++;
                }

                formulas += row.Count(SafeCsvReader.LooksLikeFormula);
            }

            var notRecognized = format == SourceFormats.Tabular ? null : AnalysisProblems.NativeLayoutNotRecognized;
            return new SourceAnalysis(format, ParserStatuses.NeedsManualMapping, encodingName, delimiterName, columns ?? 0, data, different, formulas, images, notRecognized, invalidImages);
        }
        catch (CsvFormatException ex) when (ex.Code == CsvErrors.AmbiguousDelimiter)
        {
            // Da para ler: so nao da para adivinhar. Uma pessoa indica o separador no mapeamento.
            return new SourceAnalysis(format, ParserStatuses.NeedsManualMapping, encodingName, null, 0, 0, 0, 0, images, ex.Code, invalidImages);
        }
        catch (CsvFormatException ex)
        {
            return Unreadable(ex.Code);
        }
        catch (InvalidDataException)
        {
            return Unreadable(CsvErrors.CorruptContent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreadable(CsvErrors.Timeout);
        }

        SourceAnalysis Unreadable(string code) =>
            new(format, ParserStatuses.Unreadable, encodingName, delimiterName, 0, 0, 0, 0, images, code, invalidImages);
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
