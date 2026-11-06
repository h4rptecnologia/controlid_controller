using System.Text;

namespace Platform.Modules.Importation;

public sealed class CsvLimits
{
    public int MaxColumns { get; init; } = 512;

    public int MaxFieldChars { get; init; } = 64 * 1024;

    public long MaxRows { get; init; } = 5_000_000;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class CsvFormatException(string code) : Exception($"CSV invalido: {code}.")
{
    public string Code { get; } = code;

    public CsvFormatException()
        : this("invalid")
    {
    }

    public CsvFormatException(string message, Exception innerException)
        : this(message)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }
}

public static class CsvErrors
{
    public const string UnterminatedQuote = "unterminated_quote";
    public const string TooManyColumns = "too_many_columns";
    public const string FieldTooLong = "field_too_long";
    public const string TooManyRows = "too_many_rows";
    public const string UnsupportedEncoding = "unsupported_encoding";
    public const string AmbiguousDelimiter = "ambiguous_delimiter";
    public const string InvalidQuote = "invalid_quote";
    public const string Timeout = "analysis_timeout";
    public const string CorruptContent = "corrupt_content";
}

/// <summary>
/// Leitor RFC 4180 para entrada hostil. Tudo sai como texto exato: nada de numero, data ou corte de espacos, porque
/// cartao de 64 bits e zero a esquerda nao sobrevivem a conversao. Quem interpreta e o mapeamento, depois.
/// </summary>
public static class SafeCsvReader
{
    private const int DetectionSampleRows = 50;
    private static readonly char[] CandidateDelimiters = [';', ',', '\t'];

    static SafeCsvReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Com BOM UTF-8, ou se todos os bytes forem UTF-8 valido, UTF-8. Do contrario, Windows-1252. UTF-16 e recusado:
    /// um decodificador tolerante trocaria caracteres em silencio.
    /// </summary>
    public static async Task<(Encoding Encoding, string Name)> DetectEncodingAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var start = new byte[3];
        var read = await content.ReadAtLeastAsync(start, 3, throwOnEndOfStream: false, cancellationToken);
        if (read >= 2 && ((start[0] == 0xFF && start[1] == 0xFE) || (start[0] == 0xFE && start[1] == 0xFF)))
        {
            throw new CsvFormatException(CsvErrors.UnsupportedEncoding);
        }

        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        var chars = new char[8192];
        var buffer = new byte[8192];
        var valid = true;
        try
        {
            decoder.GetChars(start, 0, read, chars, 0, flush: false);
            int count;
            while ((count = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                decoder.GetChars(buffer, 0, count, chars, 0, flush: false);
            }

            decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
        }
        catch (DecoderFallbackException)
        {
            valid = false;
        }

        return valid ? (new UTF8Encoding(false), "utf-8") : (Encoding.GetEncoding(1252), "windows-1252");
    }

    /// <summary>
    /// Precisa de um unico candidato em que ao menos dois tercos das linhas da amostra tenham a largura da primeira.
    /// Uma linha torta nao invalida o arquivo: ela e contada depois. Na duvida, nao escolhe.
    /// </summary>
    public static async Task<char> DetectDelimiterAsync(TextReader reader, CsvLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);

        var sample = new StringBuilder();
        var buffer = new char[8192];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        sample.Append(buffer, 0, read);
        var text = sample.ToString();

        var consistent = new List<char>();
        foreach (var delimiter in CandidateDelimiters)
        {
            var widths = await SampleWidthsAsync(text, delimiter, limits, read == buffer.Length, cancellationToken);
            if (widths.Count > 0 && widths[0] > 1 && widths.Count(width => width == widths[0]) * 3 >= widths.Count * 2)
            {
                consistent.Add(delimiter);
            }
        }

        return consistent.Count == 1 ? consistent[0] : throw new CsvFormatException(CsvErrors.AmbiguousDelimiter);
    }

    private static async Task<List<int>> SampleWidthsAsync(string text, char delimiter, CsvLimits limits, bool truncated, CancellationToken cancellationToken)
    {
        var widths = new List<int>();
        var rows = new List<IReadOnlyList<string>>();
        try
        {
            using var sampleReader = new StringReader(text);
            await foreach (var row in ReadRowsAsync(sampleReader, delimiter, limits, cancellationToken))
            {
                rows.Add(row);
                if (rows.Count > DetectionSampleRows)
                {
                    break;
                }
            }
        }
        catch (CsvFormatException)
        {
            // Valem as linhas ja lidas: a amostra pode ter cortado um campo, e o defeito real aparece na leitura completa.
        }

        // Com a amostra cortada, a ultima linha pode estar incompleta.
        var usable = truncated && rows.Count > 1 ? rows.SkipLast(1) : rows;
        widths.AddRange(usable.Select(r => r.Count));
        return widths;
    }

    public static async IAsyncEnumerable<IReadOnlyList<string>> ReadRowsAsync(
        TextReader reader,
        char delimiter,
        CsvLimits limits,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);

        var buffer = new char[16 * 1024];
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var fieldStarted = false;
        var rows = 0L;
        var pendingCr = false;
        var firstChar = true;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (firstChar)
                {
                    firstChar = false;
                    if (c == '\uFEFF')
                    {
                        continue;
                    }
                }

                if (pendingCr)
                {
                    pendingCr = false;
                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                    else
                    {
                        Append(field, c, limits);
                    }

                    continue;
                }

                if (afterQuote && c == '"')
                {
                    // Aspas duplicadas viram uma aspa literal.
                    Append(field, '"', limits);
                    inQuotes = true;
                    afterQuote = false;
                    continue;
                }

                if (c == delimiter)
                {
                    AddField(row, field, limits);
                    afterQuote = false;
                    fieldStarted = true;
                    continue;
                }

                if (c is '\n' or '\r')
                {
                    pendingCr = c == '\r';
                    if (row.Count > 0 || field.Length > 0 || fieldStarted || afterQuote)
                    {
                        AddField(row, field, limits);
                        if (++rows > limits.MaxRows)
                        {
                            throw new CsvFormatException(CsvErrors.TooManyRows);
                        }

                        yield return row;
                        row = [];
                    }

                    afterQuote = false;
                    fieldStarted = false;
                    continue;
                }

                if (afterQuote)
                {
                    throw new CsvFormatException(CsvErrors.InvalidQuote);
                }

                if (c == '"' && field.Length == 0)
                {
                    inQuotes = true;
                    continue;
                }

                Append(field, c, limits);
            }
        }

        if (inQuotes)
        {
            throw new CsvFormatException(CsvErrors.UnterminatedQuote);
        }

        if (row.Count > 0 || field.Length > 0 || fieldStarted || afterQuote)
        {
            AddField(row, field, limits);
            if (++rows > limits.MaxRows)
            {
                throw new CsvFormatException(CsvErrors.TooManyRows);
            }

            yield return row;
        }
    }

    /// <summary>
    /// Celula que uma planilha executaria como formula. So e contada: alterar o dado aqui destruiria o valor original.
    /// A protecao vale ao exportar. Numero negativo nao conta.
    /// </summary>
    public static bool LooksLikeFormula(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            return false;
        }

        return !decimal.TryParse(value, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static void Append(StringBuilder field, char c, CsvLimits limits)
    {
        if (field.Length >= limits.MaxFieldChars)
        {
            throw new CsvFormatException(CsvErrors.FieldTooLong);
        }

        field.Append(c);
    }

    private static void AddField(List<string> row, StringBuilder field, CsvLimits limits)
    {
        if (row.Count >= limits.MaxColumns)
        {
            throw new CsvFormatException(CsvErrors.TooManyColumns);
        }

        row.Add(field.ToString());
        field.Clear();
    }
}
