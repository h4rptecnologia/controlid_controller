using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Modules.Importation;

public static class NativeRowErrors
{
    public const string WidthMismatch = "width_mismatch";
    public const string MissingRequired = "missing_required";
    public const string InvalidInteger = "invalid_integer";
    public const string InvalidUnsigned = "invalid_unsigned_integer";
    public const string InvalidBase64 = "invalid_base64";
    public const string InvalidBoolean = "invalid_boolean";
    public const string InvalidTimestamp = "invalid_timestamp";
    public const string InvalidValidityRange = "invalid_validity_range";
    public const string DuplicateKey = "duplicate_key";
    public const string DuplicateCredential = "duplicate_credential";
}

/// <summary>Uma linha lida. Vive so em memoria: quem a recebe decide o que guardar e como cifrar.</summary>
/// <param name="Fields">Campos da documentacao, com o texto exato. Campos "nunca" nao chegam aqui.</param>
/// <param name="Extension">Colunas desconhecidas, preservadas sem interpretar.</param>
public sealed record NativeRecord(
    string ObjectName,
    long RowNumber,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Extension,
    IReadOnlyList<string> Errors);

/// <summary>Resumo por objeto. So nomes da documentacao e contagens: coluna desconhecida pode ser dado pessoal.</summary>
public sealed record NativeObjectSummary(
    string Name,
    long Rows,
    long RowsWithErrors,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> SensitiveColumns,
    IReadOnlyList<string> DiscardedColumns,
    IReadOnlyList<string> MissingRequiredColumns,
    int UnknownColumns,
    IReadOnlyDictionary<string, long> Errors);

public sealed record NativeParseSummary(
    string ParserId,
    IReadOnlyList<NativeObjectSummary> Objects,
    int UnknownSections,
    long UnknownSectionRows,
    long RowsOutsideSections,
    IReadOnlyDictionary<string, long> PendingReferences,
    long FormulaLikeCells,
    bool DuplicateTrackingTruncated);

/// <summary>Como o arquivo esta organizado: secoes marcadas pelo nome do objeto, ou uma unica tabela.</summary>
public sealed record NativeLayout(char Delimiter, bool Sectioned, string? FlatObject);

/// <summary>
/// Le o CSV pelos nomes da Lista de Objetos oficial. A documentacao nao descreve o layout nem as diferencas entre V5 e V6:
/// este parser reconhece o que os nomes permitem provar e NAO foi validado contra arquivo real de equipamento. Por isso
/// nao declara linha de firmware. O que nao bate cai em mapeamento manual, sem adivinhar.
/// </summary>
public static partial class NativeCsvParser
{
    public const string ParserId = "controlid_doc_v1";

    private static readonly char[] Delimiters = [';', ',', '\t'];

    private static readonly HashSet<string> TimestampFields = new(StringComparer.Ordinal) { "time", "timestamp", "begin_time", "end_time", "image_timestamp", "last_access" };

    /// <summary>Referencias conferidas quando o objeto de destino esta no mesmo arquivo. Logs ficam de fora: citam usuarios ja removidos.</summary>
    private static readonly Dictionary<(string Object, string Field), string> References = new()
    {
        [("templates", "user_id")] = "users",
        [("cards", "user_id")] = "users",
        [("qrcodes", "user_id")] = "users",
        [("uhf_tags", "user_id")] = "users",
        [("pins", "user_id")] = "users",
        [("user_roles", "user_id")] = "users",
        [("custom_thresholds", "user_id")] = "users",
        [("user_groups", "user_id")] = "users",
        [("user_groups", "group_id")] = "groups",
        [("user_access_rules", "user_id")] = "users",
        [("user_access_rules", "access_rule_id")] = "access_rules",
        [("group_access_rules", "group_id")] = "groups",
        [("group_access_rules", "access_rule_id")] = "access_rules",
        [("portal_access_rules", "portal_id")] = "portals",
        [("portal_access_rules", "access_rule_id")] = "access_rules",
        [("area_access_rules", "area_id")] = "areas",
        [("area_access_rules", "access_rule_id")] = "access_rules",
        [("scheduled_unlock_access_rules", "scheduled_unlock_id")] = "scheduled_unlocks",
        [("scheduled_unlock_access_rules", "access_rule_id")] = "access_rules",
        [("access_rule_time_zones", "access_rule_id")] = "access_rules",
        [("access_rule_time_zones", "time_zone_id")] = "time_zones",
        [("alarm_zone_time_zones", "time_zone_id")] = "time_zones",
        [("time_spans", "time_zone_id")] = "time_zones",
        [("portals", "area_from_id")] = "areas",
        [("portals", "area_to_id")] = "areas",
        [("contingency_card_access_rules", "access_rule_id")] = "access_rules",
    };

    /// <summary>Credencial repetida no mesmo equipamento seria ambigua na identificacao.</summary>
    private static readonly HashSet<string> UniqueCredentialObjects = new(StringComparer.Ordinal) { "cards", "qrcodes", "uhf_tags" };

    /// <summary>Sem marcador de secao, so aceita tabela unica se estas colunas estiverem la.</summary>
    private static readonly Dictionary<string, string[][]> FlatSignatures = new(StringComparer.Ordinal)
    {
        ["users"] = [["id", "name"], ["id", "registration"]],
        ["access_logs"] = [["id", "time", "event"]],
    };

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Identifier();

    /// <summary>
    /// Exige um unico delimitador sob o qual o inicio do arquivo faca sentido. Nulo significa nao reconhecido.
    /// Nao reaproveita a regra de largura de <see cref="SafeCsvReader"/>: com secoes, as larguras variam de proposito.
    /// </summary>
    public static async Task<NativeLayout?> DetectLayoutAsync(Func<Task<TextReader>> open, string? flatObject, CsvLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(limits);

        var matches = new List<NativeLayout>();
        foreach (var delimiter in Delimiters)
        {
            var rows = new List<IReadOnlyList<string>>(2);
            using var reader = await open();
            try
            {
                await foreach (var row in SafeCsvReader.ReadRowsAsync(reader, delimiter, limits, cancellationToken))
                {
                    rows.Add(row);
                    if (rows.Count == 2)
                    {
                        break;
                    }
                }
            }
            catch (CsvFormatException)
            {
                // Sob este delimitador nao faz sentido. O defeito real, se houver, aparece na leitura completa.
                continue;
            }

            if (Classify(rows, delimiter, flatObject) is { } layout)
            {
                matches.Add(layout);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static NativeLayout? Classify(List<IReadOnlyList<string>> rows, char delimiter, string? flatObject)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        if (rows.Count == 2 && MarkerName(rows[0]) is { } marker)
        {
            var known = NativeObjectSchema.Objects.TryGetValue(marker, out var definition);
            if (known ? IsHeaderOf(rows[1], definition!) : LooksLikeHeader(rows[1]))
            {
                return new NativeLayout(delimiter, Sectioned: true, null);
            }
        }

        if (flatObject is not null
            && IsHeaderOf(rows[0], NativeObjectSchema.Objects[flatObject])
            && FlatSignatures[flatObject].Any(signature => signature.All(rows[0].Contains)))
        {
            return new NativeLayout(delimiter, Sectioned: false, flatObject);
        }

        return null;
    }

    private static string? MarkerName(IReadOnlyList<string> row) =>
        row.Count > 0 && Identifier().IsMatch(row[0]) && row.Skip(1).All(cell => cell.Length == 0) ? row[0] : null;

    private static bool LooksLikeHeader(IReadOnlyList<string> row) =>
        row.Count > 0 && row.All(cell => Identifier().IsMatch(cell)) && row.Distinct(StringComparer.Ordinal).Count() == row.Count;

    /// <summary>Colunas distintas, e ao menos metade reconhecida no objeto. Coluna a mais nao invalida: fica em extensao.</summary>
    private static bool IsHeaderOf(IReadOnlyList<string> row, NativeObject definition)
    {
        if (row.Count < Math.Min(2, definition.Fields.Count) || row.Distinct(StringComparer.Ordinal).Count() != row.Count)
        {
            return false;
        }

        var known = row.Count(definition.Fields.ContainsKey);
        return known > 0 && known * 2 >= row.Count;
    }

    public static async Task<NativeParseSummary> ParseAsync(
        TextReader reader,
        NativeLayout layout,
        CsvLimits limits,
        Func<NativeRecord, CancellationToken, ValueTask>? sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(limits);

        var state = new ParseState(sink, limits.MaxTrackedValues);
        IReadOnlyList<string>? pendingMarker = null;
        var pendingRow = 0L;
        var rowNumber = 0L;
        var first = true;

        await foreach (var row in SafeCsvReader.ReadRowsAsync(reader, layout.Delimiter, limits, cancellationToken))
        {
            rowNumber++;

            if (!layout.Sectioned && first)
            {
                first = false;
                state.OpenSection(layout.FlatObject!, row);
                continue;
            }

            if (pendingMarker is not null)
            {
                var name = pendingMarker[0];
                var known = NativeObjectSchema.Objects.TryGetValue(name, out var definition);
                if (known ? IsHeaderOf(row, definition!) : LooksLikeHeader(row))
                {
                    if (known)
                    {
                        state.OpenSection(name, row);
                    }
                    else
                    {
                        state.OpenUnknownSection();
                    }

                    pendingMarker = null;
                    continue;
                }

                // Parecia marcador, mas nao veio cabecalho: era uma linha comum.
                await state.DataRowAsync(pendingMarker, pendingRow, cancellationToken);
                pendingMarker = null;
            }

            if (layout.Sectioned && MarkerName(row) is not null)
            {
                pendingMarker = row;
                pendingRow = rowNumber;
                continue;
            }

            await state.DataRowAsync(row, rowNumber, cancellationToken);
        }

        if (pendingMarker is not null)
        {
            await state.DataRowAsync(pendingMarker, pendingRow, cancellationToken);
        }

        return state.Summary();
    }

    private sealed class ParseState(Func<NativeRecord, CancellationToken, ValueTask>? sink, int maxTracked)
    {
        private readonly Dictionary<string, SectionStats> stats = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> keysByObject = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> credentialsByObject = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Object, string Field), HashSet<string>> referencesSeen = [];
        private SectionStats? current;
        private bool inUnknownSection;
        private int unknownSections;
        private long unknownSectionRows;
        private long rowsOutside;
        private long formulas;
        private bool truncated;

        public void OpenSection(string name, IReadOnlyList<string> header)
        {
            inUnknownSection = false;
            if (!stats.TryGetValue(name, out var section))
            {
                var definition = NativeObjectSchema.Objects[name];
                section = new SectionStats(definition);
                stats[name] = section;
            }

            section.Header = header;
            section.UnknownColumns = Math.Max(section.UnknownColumns, header.Count(column => !section.Definition.Fields.ContainsKey(column)));
            section.KnownColumns.UnionWith(header.Where(section.Definition.Fields.ContainsKey));
            current = section;
        }

        public void OpenUnknownSection()
        {
            current = null;
            inUnknownSection = true;
            unknownSections++;
        }

        public async ValueTask DataRowAsync(IReadOnlyList<string> row, long rowNumber, CancellationToken cancellationToken)
        {
            formulas += row.Count(SafeCsvReader.LooksLikeFormula);

            if (inUnknownSection)
            {
                unknownSectionRows++;
                return;
            }

            if (current is null)
            {
                rowsOutside++;
                return;
            }

            current.Rows++;
            var errors = new List<string>();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var extension = new Dictionary<string, string>(StringComparer.Ordinal);

            if (row.Count != current.Header.Count)
            {
                errors.Add(NativeRowErrors.WidthMismatch);
            }
            else
            {
                for (var i = 0; i < row.Count; i++)
                {
                    var column = current.Header[i];
                    if (!current.Definition.Fields.TryGetValue(column, out var field))
                    {
                        extension[column] = row[i];
                    }
                    else if (field.Sensitivity != NativeFieldSensitivity.Never)
                    {
                        ValidateField(field, row[i], errors);
                        fields[column] = row[i];
                    }
                }

                CheckValidity(current.Definition.Name, fields, errors);
                Track(current.Definition, fields, errors);
            }

            if (errors.Count > 0)
            {
                current.RowsWithErrors++;
                foreach (var code in errors)
                {
                    current.Errors[code] = current.Errors.GetValueOrDefault(code) + 1;
                }
            }

            if (sink is not null)
            {
                await sink(new NativeRecord(current.Definition.Name, rowNumber, fields, extension, errors), cancellationToken);
            }
        }

        private static void ValidateField(NativeField field, string value, List<string> errors)
        {
            if (value.Length == 0)
            {
                if (field.Required)
                {
                    errors.Add(NativeRowErrors.MissingRequired);
                }

                return;
            }

            switch (field.Kind)
            {
                case NativeFieldKind.Whole when !IsDigits(value, allowSign: true) || !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _):
                    errors.Add(NativeRowErrors.InvalidInteger);
                    return;
                case NativeFieldKind.UnsignedWhole when !IsDigits(value, allowSign: false) || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _):
                    errors.Add(NativeRowErrors.InvalidUnsigned);
                    return;
                case NativeFieldKind.Base64 when !Base64.IsValid(value):
                    errors.Add(NativeRowErrors.InvalidBase64);
                    return;
                case NativeFieldKind.Boolean when value is not ("0" or "1" or "true" or "false"):
                    errors.Add(NativeRowErrors.InvalidBoolean);
                    return;
            }

            if (TimestampFields.Contains(field.Name) && field.Kind == NativeFieldKind.Whole && value[0] == '-')
            {
                errors.Add(NativeRowErrors.InvalidTimestamp);
            }
        }

        /// <summary>A documentacao define 0 como "sem verificacao": so compara quando as duas pontas existem.</summary>
        private static void CheckValidity(string objectName, Dictionary<string, string> fields, List<string> errors)
        {
            if (objectName == "users"
                && fields.TryGetValue("begin_time", out var begin) && long.TryParse(begin, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var from) && from > 0
                && fields.TryGetValue("end_time", out var end) && long.TryParse(end, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var to) && to > 0
                && from > to)
            {
                errors.Add(NativeRowErrors.InvalidValidityRange);
            }
        }

        private void Track(NativeObject definition, Dictionary<string, string> fields, List<string> errors)
        {
            if (definition.Key.All(fields.ContainsKey))
            {
                var key = string.Join('\u001f', definition.Key.Select(k => fields[k]));
                if (key.Replace("\u001f", string.Empty, StringComparison.Ordinal).Length > 0 && !Remember(keysByObject, definition.Name, key))
                {
                    errors.Add(NativeRowErrors.DuplicateKey);
                }
            }

            if (UniqueCredentialObjects.Contains(definition.Name)
                && fields.TryGetValue("value", out var credential) && credential.Length > 0
                && !Remember(credentialsByObject, definition.Name, credential))
            {
                errors.Add(NativeRowErrors.DuplicateCredential);
            }

            foreach (var (field, value) in fields)
            {
                if (value.Length > 0 && References.ContainsKey((definition.Name, field)))
                {
                    if (!referencesSeen.TryGetValue((definition.Name, field), out var seen))
                    {
                        seen = new HashSet<string>(StringComparer.Ordinal);
                        referencesSeen[(definition.Name, field)] = seen;
                    }

                    if (seen.Count < maxTracked)
                    {
                        seen.Add(value);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
        }

        /// <summary>Falso se ja existia. Acima do teto para de lembrar e avisa: memoria nao pode crescer com a entrada.</summary>
        private bool Remember(Dictionary<string, HashSet<string>> sets, string name, string value)
        {
            if (!sets.TryGetValue(name, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                sets[name] = set;
            }

            if (set.Count >= maxTracked && !set.Contains(value))
            {
                truncated = true;
                return true;
            }

            return set.Add(value);
        }

        public NativeParseSummary Summary()
        {
            var pending = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var ((objectName, field), seen) in referencesSeen)
            {
                var target = References[(objectName, field)];

                // Destino fora do arquivo nao e erro: users.csv nao precisa trazer grupos.
                if (stats.TryGetValue(target, out var targetStats) && targetStats.KnownColumns.Contains("id") && keysByObject.TryGetValue(target, out var keys))
                {
                    var missing = seen.Count(value => !keys.Contains(value));
                    if (missing > 0)
                    {
                        pending[$"{objectName}.{field}"] = missing;
                    }
                }
            }

            var objects = stats.Values.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).Select(s => new NativeObjectSummary(
                s.Definition.Name,
                s.Rows,
                s.RowsWithErrors,
                s.KnownColumns.Order(StringComparer.Ordinal).ToList(),
                s.KnownColumns.Where(c => s.Definition.Fields[c].Sensitivity == NativeFieldSensitivity.Sensitive).Order(StringComparer.Ordinal).ToList(),
                s.KnownColumns.Where(c => s.Definition.Fields[c].Sensitivity == NativeFieldSensitivity.Never).Order(StringComparer.Ordinal).ToList(),
                s.Definition.Fields.Values.Where(f => f.Required && !s.KnownColumns.Contains(f.Name)).Select(f => f.Name).Order(StringComparer.Ordinal).ToList(),
                s.UnknownColumns,
                new SortedDictionary<string, long>(s.Errors, StringComparer.Ordinal))).ToList();

            // Com a memoria cortada, "nao encontrado" deixa de ser prova: melhor nao afirmar do que apontar falsa pendencia.
            if (truncated)
            {
                pending.Clear();
            }

            return new NativeParseSummary(ParserId, objects, unknownSections, unknownSectionRows, rowsOutside, pending, formulas, truncated);
        }
    }

    private sealed class SectionStats(NativeObject definition)
    {
        public NativeObject Definition { get; } = definition;

        public IReadOnlyList<string> Header { get; set; } = [];

        public HashSet<string> KnownColumns { get; } = new(StringComparer.Ordinal);

        public int UnknownColumns { get; set; }

        public long Rows { get; set; }

        public long RowsWithErrors { get; set; }

        public Dictionary<string, long> Errors { get; } = new(StringComparer.Ordinal);
    }

    private static bool IsDigits(string value, bool allowSign)
    {
        var start = allowSign && value[0] == '-' ? 1 : 0;
        if (start == value.Length)
        {
            return false;
        }

        for (var i = start; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
