using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Platform.Modules.DeviceIntegration.ControlId;

/// <summary>
/// O texto de "Carregar Objetos" descreve where como objeto, e o exemplo da mesma pagina usa lista.
/// Qual vale em cada firmware so se descobre em hardware; ver contratos-fabricante.md.
/// </summary>
public enum WhereDialect
{
    ObjectMap,
    ConditionList,
}

public enum PagingMode
{
    /// <summary>Por id crescente. Insercoes e remocoes durante a leitura nao causam repeticao nem perda.</summary>
    Keyset,

    /// <summary>
    /// Por offset, em objetos sem id. Remocao concorrente pode pular linha; o resultado e melhor esforco e
    /// precisa de reconciliacao, nao serve como prova de ausencia.
    /// </summary>
    OffsetBestEffort,
}

/// <summary>Posicao de leitura, feita para ser persistida como checkpoint.</summary>
public sealed record DeviceObjectCursor(string? LastId, long Offset);

public sealed record LoadObjectsRequest(
    string ObjectName,
    DeviceObjectCursor? Cursor = null,
    int PageSize = 200,
    IReadOnlyCollection<string>? IncludeSensitive = null);

/// <summary>Campo sensivel so sai por chamada explicita: nao aparece em ToString nem em serializacao.</summary>
public sealed class SensitiveFields
{
    private readonly Dictionary<string, string> values;

    internal SensitiveFields(Dictionary<string, string> values) => this.values = values;

    public int Count => values.Count;

    public string? Reveal(string field) => values.GetValueOrDefault(field);

    public override string ToString() => $"SensitiveFields({values.Count} campos ocultos)";
}

/// <param name="SourceKey">Texto exato do equipamento: nunca convertido para numero de ponto flutuante.</param>
/// <param name="Fields">Somente campos classificados como seguros, com o valor original preservado.</param>
public sealed record DeviceObjectRow(
    string SourceKey,
    IReadOnlyDictionary<string, JsonElement> Fields,
    SensitiveFields Sensitive);

/// <param name="UnclassifiedFields">Nomes recebidos fora do catalogo. Os valores foram descartados.</param>
/// <param name="ProjectionIgnored">O equipamento enviou campo sensivel que nao foi pedido.</param>
public sealed record DeviceObjectPage(
    string ObjectName,
    IReadOnlyList<DeviceObjectRow> Rows,
    DeviceObjectCursor NextCursor,
    bool HasMore,
    PagingMode Paging,
    IReadOnlyList<string> UnclassifiedFields,
    bool ProjectionIgnored);

/// <summary>
/// Numero de cartao conforme a documentacao oficial: valor na API = area * 2^32 + numero.
/// Exemplo oficial: 123,45678 corresponde a 528281023086.
/// </summary>
public readonly record struct WiegandCard(uint Area, uint Number)
{
    private const int NumberBits = 32;

    public string ToApiValue() => (((ulong)Area << NumberBits) + Number).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static WiegandCard FromApiValue(string apiValue)
    {
        if (!IsUnsignedInteger(apiValue) || !ulong.TryParse(apiValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException("Valor de cartao invalido.");
        }

        return new WiegandCard((uint)(value >> NumberBits), (uint)value);
    }

    private static bool IsUnsignedInteger(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);
}

internal static class DeviceObjectRequestBuilder
{
    public static byte[] Build(DeviceObjectDefinition definition, LoadObjectsRequest request, WhereDialect dialect, PagingMode mode)
    {
        var requested = definition.Safe.Concat(request.IncludeSensitive ?? []).Distinct(StringComparer.Ordinal).ToList();

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("object", definition.Name);

            // Lista explicita: o que nao se pede o equipamento nao envia (minimizacao).
            json.WriteStartArray("fields");
            foreach (var field in requested)
            {
                json.WriteStringValue(field);
            }

            json.WriteEndArray();
            json.WriteNumber("limit", request.PageSize);

            // Ordem estavel. O valor especial "ascending" e o documentado para a direcao.
            json.WriteStartArray("order");
            json.WriteStringValue("ascending");
            foreach (var key in definition.Key)
            {
                json.WriteStringValue(key);
            }

            json.WriteEndArray();

            if (mode == PagingMode.OffsetBestEffort)
            {
                json.WriteNumber("offset", request.Cursor?.Offset ?? 0);
            }
            else if (request.Cursor?.LastId is { } last)
            {
                WriteWhere(json, definition.Name, last, dialect);
            }

            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteWhere(Utf8JsonWriter json, string objectName, string lastId, WhereDialect dialect)
    {
        // Cursor ja validado como inteiro; sai sem aspas e sem passar por double.
        var value = Encoding.UTF8.GetBytes(lastId);
        if (dialect == WhereDialect.ObjectMap)
        {
            json.WriteStartObject("where");
            json.WriteStartObject(objectName);
            json.WriteStartObject("id");
            json.WritePropertyName(">");
            json.WriteRawValue(value, skipInputValidation: false);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            return;
        }

        json.WriteStartArray("where");
        json.WriteStartObject();
        json.WriteString("object", objectName);
        json.WriteString("field", "id");
        json.WriteString("operator", ">");
        json.WritePropertyName("value");
        json.WriteRawValue(value, skipInputValidation: false);
        json.WriteEndObject();
        json.WriteEndArray();
    }
}

internal static class DeviceObjectResponseParser
{
    public static DeviceObjectPage Parse(
        JsonDocument document,
        DeviceObjectDefinition definition,
        LoadObjectsRequest request,
        PagingMode mode)
    {
        if (!document.RootElement.TryGetProperty(definition.Name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new DeviceCallException(DeviceFailure.InvalidResponse);
        }

        if (array.GetArrayLength() > request.PageSize)
        {
            // Limite ignorado: sem isso o cursor e a memoria ficariam sem controle.
            throw new DeviceCallException(DeviceFailure.UnexpectedBehavior);
        }

        var safe = definition.Safe.ToHashSet(StringComparer.Ordinal);
        var sensitive = definition.Sensitive.ToHashSet(StringComparer.Ordinal);
        var never = definition.Never.ToHashSet(StringComparer.Ordinal);
        var allowedSensitive = (request.IncludeSensitive ?? []).ToHashSet(StringComparer.Ordinal);

        var rows = new List<DeviceObjectRow>(array.GetArrayLength());
        var unclassified = new SortedSet<string>(StringComparer.Ordinal);
        var projectionIgnored = false;
        BigInteger? previous = request.Cursor?.LastId is { } last ? ParseId(last) : null;
        string? lastId = request.Cursor?.LastId;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new DeviceCallException(DeviceFailure.InvalidResponse);
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var hidden = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in item.EnumerateObject())
            {
                if (safe.Contains(property.Name))
                {
                    fields[property.Name] = property.Value.Clone();
                }
                else if (allowedSensitive.Contains(property.Name))
                {
                    hidden[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
                }
                else if (sensitive.Contains(property.Name) || never.Contains(property.Name))
                {
                    projectionIgnored = true;
                }
                else
                {
                    unclassified.Add(property.Name);
                }
            }

            var key = BuildKey(definition, fields);
            if (mode == PagingMode.Keyset)
            {
                // Filtro ou ordem ignorados: repetiria dados sem fim. Melhor parar do que corromper o historico.
                var id = ParseId(key);
                if (previous is { } before && id <= before)
                {
                    throw new DeviceCallException(DeviceFailure.UnexpectedBehavior);
                }

                previous = id;
                lastId = key;
            }

            rows.Add(new DeviceObjectRow(key, fields, new SensitiveFields(hidden)));
        }

        var nextOffset = mode == PagingMode.OffsetBestEffort ? (request.Cursor?.Offset ?? 0) + rows.Count : 0;
        return new DeviceObjectPage(
            definition.Name,
            rows,
            new DeviceObjectCursor(lastId, nextOffset),
            rows.Count == request.PageSize,
            mode,
            unclassified.ToList(),
            projectionIgnored);
    }

    public static BigInteger ParseId(string text)
    {
        if (text.Length == 0 || !text.TrimStart('-').All(char.IsAsciiDigit) || !BigInteger.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new DeviceCallException(DeviceFailure.InvalidResponse);
        }

        return value;
    }

    private static string BuildKey(DeviceObjectDefinition definition, Dictionary<string, JsonElement> fields)
    {
        var parts = new List<string>(definition.Key.Count);
        foreach (var name in definition.Key)
        {
            if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number)
            {
                throw new DeviceCallException(DeviceFailure.InvalidResponse);
            }

            parts.Add(value.GetRawText());
        }

        return string.Join(':', parts);
    }
}
