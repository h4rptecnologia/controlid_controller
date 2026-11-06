using System.Collections.Frozen;

namespace Platform.Shared.Contracts;

/// <summary>Evento ja saneado pelo gateway. Nunca carrega o corpo original do equipamento.</summary>
/// <param name="Id">Gerado no gateway ao receber; permite reenviar o lote sem duplicar.</param>
/// <param name="Family">Familia em <see cref="EdgeEventFamilies"/>.</param>
/// <param name="SourceDeviceId">O device_id declarado pelo equipamento. Nulo quando o evento nao traz, como em secbox.</param>
public sealed record EdgeEvent(
    Guid Id,
    Guid DeviceId,
    string Family,
    string? ChangeType,
    string? SourceEventId,
    string? SourceUuid,
    string? SourceDeviceId,
    long? OccurredAtUnix,
    DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, string> Fields,
    string? Ingress = null);

public sealed record EdgeEventBatch(Guid BatchId, IReadOnlyList<EdgeEvent> Events);

public sealed record RejectedEdgeEvent(Guid Id, string Code);

public sealed record EdgeEventBatchResult(IReadOnlyList<Guid> Accepted, IReadOnlyList<RejectedEdgeEvent> Rejected);

public sealed record EdgeDevice(Guid DeviceId, string Host);

public sealed record EdgeDeviceList(IReadOnlyList<EdgeDevice> Devices);

public static class EdgeEventIngress
{
    public const string Monitor = "monitor";
    public const string Api = "api";
}

public static class EdgeEventFamilies
{
    public const string AccessLogs = "access_logs";
    public const string AlarmLogs = "alarm_logs";
    public const string AccessEvents = "access_events";
    public const string Templates = "templates";
    public const string Cards = "cards";
    public const string Door = "door";
    public const string SecBox = "secbox";
    public const string CatraEvent = "catra_event";
    public const string OperationMode = "operation_mode";
    public const string UsbDrive = "usb_drive";
    public const string DeviceAlive = "device_is_alive";

    /// <summary>
    /// Lista permitida por familia, aplicada no gateway e de novo na central. Fora dela, o valor nao passa:
    /// access_logs traz pin_value, qrcode_value, uhf_tag e card_value; templates e cards trazem o proprio segredo.
    /// </summary>
    public static readonly FrozenDictionary<string, FrozenSet<string>> AllowedFields = new Dictionary<string, FrozenSet<string>>
    {
        [AccessLogs] = Set("id", "time", "event", "device_id", "identifier_id", "user_id", "portal_id", "identification_rule_id", "log_type_id"),
        [AlarmLogs] = Set("id", "event", "cause", "user_id", "time", "access_log_id", "door_id"),
        [AccessEvents] = Set("id", "event", "type", "identification", "device_id", "timestamp"),
        [Templates] = Set("id", "user_id"),
        [Cards] = Set("id", "user_id"),
        [Door] = Set("id", "open", "access_event_id", "time"),
        [SecBox] = Set("id", "open", "access_event_id"),
        [CatraEvent] = Set("type", "name", "time", "uuid", "access_event_id"),
        [OperationMode] = Set("mode", "mode_name", "time", "last_offline", "exception_mode"),
        [UsbDrive] = Set("event", "time"),
        [DeviceAlive] = Set("access_logs", "time"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenSet<string> Set(params string[] names) => names.ToFrozenSet(StringComparer.Ordinal);
}
