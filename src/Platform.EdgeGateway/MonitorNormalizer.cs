using System.Globalization;
using System.Text.Json;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

/// <summary>
/// Formatos de https://www.controlid.com.br/docs/access-api-pt/monitor/introducao-ao-monitor/ . Tudo passa por lista
/// permitida antes de chegar ao disco, e o corpo original e descartado. Sem amostra real de firmware (a homologar).
/// </summary>
public static class MonitorNormalizer
{
    private static readonly HashSet<string> DaoObjects = new(StringComparer.Ordinal)
    {
        EdgeEventFamilies.AccessLogs, EdgeEventFamilies.AlarmLogs, EdgeEventFamilies.Templates, EdgeEventFamilies.Cards,
    };

    /// <summary>Nulo: JSON invalido ou fora do formato esperado para a rota.</summary>
    public static IReadOnlyList<EdgeEvent>? Normalize(string route, ReadOnlySpan<byte> body, Guid deviceId, DateTimeOffset observedAt)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var declared = Scalar(root, "device_id");
            return route switch
            {
                "dao" => FromDao(root, deviceId, declared, observedAt),
                "door" => Nested(root, "door", EdgeEventFamilies.Door, deviceId, declared, observedAt, withAccessEvent: true),
                "secbox" => Nested(root, "secbox", EdgeEventFamilies.SecBox, deviceId, declared, observedAt, withAccessEvent: true),
                "catra_event" => FromCatra(root, deviceId, declared, observedAt),
                "operation_mode" => Nested(root, "operation_mode", EdgeEventFamilies.OperationMode, deviceId, declared, observedAt, withAccessEvent: false),
                "usb_drive" => Nested(root, "usb_drive", EdgeEventFamilies.UsbDrive, deviceId, declared, observedAt, withAccessEvent: false),
                "device_is_alive" => FromAlive(root, deviceId, declared, observedAt),
                _ => null,
            };
        }
    }

    private static List<EdgeEvent>? FromDao(JsonElement root, Guid deviceId, string? declared, DateTimeOffset observedAt)
    {
        if (!root.TryGetProperty("object_changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var events = new List<EdgeEvent>();
        foreach (var change in changes.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object || Scalar(change, "object") is not { } family || !DaoObjects.Contains(family)
                || !change.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            {
                // Objeto novo de firmware: nao ha lista permitida para ele, entao nada passa.
                continue;
            }

            var fields = Allowed(values, family);
            events.Add(new EdgeEvent(
                Guid.NewGuid(),
                deviceId,
                family,
                Scalar(change, "type"),
                fields.GetValueOrDefault("id"),
                null,
                declared,
                Unix(fields.GetValueOrDefault("time")),
                observedAt,
                fields));
        }

        return events;
    }

    private static List<EdgeEvent>? Nested(JsonElement root, string property, string family, Guid deviceId, string? declared, DateTimeOffset observedAt, bool withAccessEvent)
    {
        if (!root.TryGetProperty(property, out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = Allowed(inner, family);
        var time = Scalar(root, "time") ?? fields.GetValueOrDefault("time");
        if (time is not null && EdgeEventFamilies.AllowedFields[family].Contains("time"))
        {
            fields["time"] = time;
        }

        if (withAccessEvent && Scalar(root, "access_event_id") is { } accessEvent)
        {
            fields["access_event_id"] = accessEvent;
        }

        return [new EdgeEvent(Guid.NewGuid(), deviceId, family, null, null, null, declared, Unix(time), observedAt, fields)];
    }

    private static List<EdgeEvent>? FromCatra(JsonElement root, Guid deviceId, string? declared, DateTimeOffset observedAt)
    {
        if (!root.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = Allowed(inner, EdgeEventFamilies.CatraEvent);
        if (Scalar(root, "access_event_id") is { } accessEvent)
        {
            fields["access_event_id"] = accessEvent;
        }

        var time = fields.GetValueOrDefault("time") ?? Scalar(root, "time");
        if (time is not null)
        {
            fields["time"] = time;
        }

        return [new EdgeEvent(Guid.NewGuid(), deviceId, EdgeEventFamilies.CatraEvent, null, null, fields.GetValueOrDefault("uuid"), declared, Unix(time), observedAt, fields)];
    }

    private static List<EdgeEvent> FromAlive(JsonElement root, Guid deviceId, string? declared, DateTimeOffset observedAt)
    {
        var fields = Allowed(root, EdgeEventFamilies.DeviceAlive);
        return [new EdgeEvent(Guid.NewGuid(), deviceId, EdgeEventFamilies.DeviceAlive, null, null, null, declared, Unix(fields.GetValueOrDefault("time")), observedAt, fields)];
    }

    private static Dictionary<string, string> Allowed(JsonElement element, string family)
    {
        var allowed = EdgeEventFamilies.AllowedFields[family];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (allowed.Contains(property.Name) && ScalarOf(property.Value) is { } value)
            {
                fields[property.Name] = value;
            }
        }

        return fields;
    }

    private static string? Scalar(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ScalarOf(value) : null;

    // A doc mostra os mesmos campos ora como texto, ora como numero. Tudo vira texto exato, sem passar por double.
    private static string? ScalarOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static long? Unix(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
