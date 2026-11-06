using System.IO.Compression;

namespace Platform.Modules.Importation;

public static class SourceFormats
{
    public const string Users = "controlid_users_zip";
    public const string Sync = "controlid_sync_zip";
    public const string Backup = "controlid_backup_zip";
    public const string Tabular = "tabular";
    public const string UnknownArchive = "unknown_archive";
}

public static class ParserStatuses
{
    /// <summary>Nao ha parser homologado para este arquivo. Exige mapeamento feito por uma pessoa.</summary>
    public const string NeedsManualMapping = "needs_manual_mapping";

    /// <summary>O CSV nao pode ser lido com seguranca; nem mapeamento manual resolveria.</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>
/// Somente contagens. Os nomes de coluna ficam de fora de proposito: sem cabecalho, a primeira linha seria dado pessoal
/// ou PIN num JSONB tecnico, e nenhuma heuristica distingue "Ana;Silva" de um cabecalho. Quem precisa dos nomes le o
/// original cifrado, com permissao e auditoria.
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
    string? Problem);

/// <summary>
/// Identifica o que foi enviado sem fingir que entende. A documentacao oficial descreve a estrutura do ZIP e diz que
/// os CSVs de V5 e V6 diferem, sem descrever as colunas. Sem amostras reais nao ha parser nativo: o resultado e sempre
/// mapeamento manual, e a linha de firmware fica indeterminada.
/// </summary>
public static class SourceAnalyzer
{
    private const string ImagesFolder = "user_images/";

    private static readonly Dictionary<string, string> NativeCsvNames = new(StringComparer.Ordinal)
    {
        ["users.csv"] = SourceFormats.Users,
        ["sync.csv"] = SourceFormats.Sync,
        ["backup.csv"] = SourceFormats.Backup,
    };

    public static async Task<SourceAnalysis> AnalyzeAsync(Stream content, string detectedType, CsvLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return detectedType == DetectedTypes.Zip
            ? await AnalyzeZipAsync(content, limits, cancellationToken)
            : await AnalyzeCsvAsync(() => { content.Position = 0; return Task.FromResult(new NonDisposingStream(content) as Stream); }, SourceFormats.Tabular, 0, limits, cancellationToken);
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
            return await AnalyzeCsvAsync(() => others[0].OpenAsync(cancellationToken), format, images, limits, cancellationToken);
        }

        return new SourceAnalysis(SourceFormats.UnknownArchive, ParserStatuses.NeedsManualMapping, null, null, 0, 0, 0, 0, images, null);
    }

    private static async Task<SourceAnalysis> AnalyzeCsvAsync(Func<Task<Stream>> open, string format, int images, CsvLimits limits, CancellationToken cancellationToken)
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

            return new SourceAnalysis(format, ParserStatuses.NeedsManualMapping, encodingName, delimiterName, columns ?? 0, data, different, formulas, images, null);
        }
        catch (CsvFormatException ex) when (ex.Code == CsvErrors.AmbiguousDelimiter)
        {
            // Da para ler: so nao da para adivinhar. Uma pessoa indica o separador no mapeamento.
            return new SourceAnalysis(format, ParserStatuses.NeedsManualMapping, encodingName, null, 0, 0, 0, 0, images, ex.Code);
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
            new(format, ParserStatuses.Unreadable, encodingName, delimiterName, 0, 0, 0, 0, images, code);
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
