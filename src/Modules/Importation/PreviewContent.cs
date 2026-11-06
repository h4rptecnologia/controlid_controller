using System.Globalization;
using System.IO.Compression;

namespace Platform.Modules.Importation;

public sealed record MappingColumn(int Index, string Target, string? DateFormat);

/// <summary>Mapeamento feito por uma pessoa. Guarda posicao e destino, nunca o texto do cabecalho, que pode ser dado pessoal.</summary>
public sealed record TabularMapping(string Delimiter, bool HasHeader, IReadOnlyList<MappingColumn> Columns);

public static class DateFormats
{
    public const string Iso = "iso";
    public const string Unix = "unix";
    public const string Brazilian = "br";
}

public static class TabularMappingRules
{
    private static readonly HashSet<string> Targets = new(PersonFields.All.Append("source_id"), StringComparer.Ordinal);

    /// <summary>Nulo quando valido; do contrario, um texto para a pessoa corrigir.</summary>
    public static string? Validate(TabularMapping? mapping)
    {
        if (mapping is null || mapping.Columns is not { Count: > 0 })
        {
            return "Indique ao menos o nome e a matrícula.";
        }

        if (DelimiterOf(mapping.Delimiter) is null)
        {
            return "Separador inválido: use ponto e vírgula, vírgula ou tabulação.";
        }

        if (mapping.Columns.Any(c => !Targets.Contains(c.Target)))
        {
            return "O destino de uma das colunas não existe.";
        }

        if (mapping.Columns.Any(c => c.Index < 0))
        {
            return "Coluna inválida.";
        }

        if (mapping.Columns.DistinctBy(c => c.Target).Count() != mapping.Columns.Count || mapping.Columns.DistinctBy(c => c.Index).Count() != mapping.Columns.Count)
        {
            return "Cada coluna e cada destino só podem ser usados uma vez.";
        }

        if (!mapping.Columns.Any(c => c.Target == PersonFields.DisplayName) || !mapping.Columns.Any(c => c.Target == PersonFields.Registration))
        {
            return "Indique ao menos o nome e a matrícula: nome sozinho não identifica uma pessoa.";
        }

        var isDate = (MappingColumn c) => c.Target is PersonFields.ValidFrom or PersonFields.ValidTo;
        if (mapping.Columns.Any(c => isDate(c) && c.DateFormat is not (DateFormats.Iso or DateFormats.Unix or DateFormats.Brazilian)))
        {
            return "Indique o formato das colunas de data.";
        }

        return null;
    }

    public static char? DelimiterOf(string? name) => name switch
    {
        ";" => ';',
        "," => ',',
        "tab" => '\t',
        _ => null,
    };
}

/// <summary>Transforma cada fonte em pessoas candidatas. Campos sensiveis do arquivo nao passam daqui.</summary>
public static class PersonReader
{
    private static readonly string[] Brazilian = ["dd/MM/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss"];

    public static PersonCandidate FromNative(NativeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var errors = record.Errors.ToList();
        var type = record.Fields.GetValueOrDefault("user_type_id") switch
        {
            null or "" => "employee",
            "1" => "visitor",
            _ => null,
        };

        // A pagina define so 1 (visitante) e nulo (usuario). Outro valor nao vira tipo adivinhado.
        if (type is null && !errors.Contains(NativeRowErrors.InvalidInteger))
        {
            errors.Add(PreviewCodes.UnknownUserType);
        }

        return new PersonCandidate(
            record.RowNumber,
            record.Fields.GetValueOrDefault("id"),
            record.Fields.GetValueOrDefault("registration"),
            record.Fields.GetValueOrDefault("name"),
            type ?? "employee",
            FromUnixOrZero(record.Fields.GetValueOrDefault("begin_time")),
            FromUnixOrZero(record.Fields.GetValueOrDefault("end_time")),
            errors);
    }

    public static PersonCandidate FromMapped(IReadOnlyList<string> row, long rowNumber, TabularMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(mapping);
        var errors = new List<string>();

        string? Value(string target)
        {
            var column = mapping.Columns.FirstOrDefault(c => c.Target == target);
            if (column is null)
            {
                return null;
            }

            if (column.Index >= row.Count)
            {
                if (!errors.Contains(NativeRowErrors.WidthMismatch))
                {
                    errors.Add(NativeRowErrors.WidthMismatch);
                }

                return null;
            }

            return row[column.Index];
        }

        DateTimeOffset? Date(string target)
        {
            var text = Value(target);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var format = mapping.Columns.First(c => c.Target == target).DateFormat;
            if (TryParseDate(text.Trim(), format, out var parsed))
            {
                return parsed;
            }

            if (!errors.Contains(PreviewCodes.InvalidDate))
            {
                errors.Add(PreviewCodes.InvalidDate);
            }

            return null;
        }

        var personType = Value(PersonFields.PersonType);
        if (personType is { Length: > 0 } && personType is not ("employee" or "visitor" or "contractor" or "other"))
        {
            errors.Add(PreviewCodes.InvalidPersonType);
        }

        var from = Date(PersonFields.ValidFrom);
        var to = Date(PersonFields.ValidTo);
        if (from > to)
        {
            errors.Add(NativeRowErrors.InvalidValidityRange);
        }

        var source = Value("source_id");
        return new PersonCandidate(
            rowNumber,
            string.IsNullOrEmpty(source) ? null : source,
            Value(PersonFields.Registration),
            Value(PersonFields.DisplayName),
            string.IsNullOrEmpty(personType) || errors.Contains(PreviewCodes.InvalidPersonType) ? "employee" : personType,
            from,
            to,
            errors);
    }

    private static bool TryParseDate(string text, string? format, out DateTimeOffset value)
    {
        value = default;
        switch (format)
        {
            case DateFormats.Unix when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= 253402300799:
                value = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            case DateFormats.Iso:
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
            case DateFormats.Brazilian:
                return DateTimeOffset.TryParseExact(text, Brazilian, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
            default:
                return false;
        }
    }

    /// <summary>A documentacao define 0 como "sem verificacao". Valor invalido ja foi acusado pelo parser.</summary>
    private static DateTimeOffset? FromUnixOrZero(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}

/// <summary>Da acesso ao CSV de um arquivo inspecionado, dentro do ZIP ou solto. Nao extrai nada para o disco.</summary>
public sealed class ImportCsvSource : IDisposable
{
    private readonly ZipArchive? archive;
    private readonly Func<Task<Stream>> open;

    private ImportCsvSource(ZipArchive? archive, Func<Task<Stream>> open)
    {
        this.archive = archive;
        this.open = open;
    }

    public Task<Stream> OpenAsync() => open();

    public void Dispose() => archive?.Dispose();

    /// <summary>Nulo quando nao ha um unico CSV da estrutura documentada para ler.</summary>
    public static ImportCsvSource? For(Stream content, string detectedType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (detectedType != DetectedTypes.Zip)
        {
            return new ImportCsvSource(null, () =>
            {
                content.Position = 0;
                return Task.FromResult<Stream>(new SourceAnalyzer.NonDisposingStream(content));
            });
        }

        content.Position = 0;
#pragma warning disable CA2000 // Fica com a instancia devolvida, que o descarta.
        var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
#pragma warning restore CA2000
        var entry = SourceAnalyzer.DocumentedCsvEntry(zip);
        if (entry is null)
        {
            zip.Dispose();
            return null;
        }

        return new ImportCsvSource(zip, async () => await entry.OpenAsync(cancellationToken));
    }
}
