// Gerado do texto de https://www.controlid.com.br/docs/access-api-pt/objetos/lista-de-objetos/ em 06/11/2026.
// A classificacao de sensibilidade e nossa. Disponibilidade por modelo e firmware continua nao homologada.
using System.Collections.Frozen;

namespace Platform.Modules.DeviceIntegration.ControlId;

/// <param name="Key">Campos que identificam a linha. Com "id" a leitura usa cursor; sem ele, offset.</param>
/// <param name="Safe">Pode ser pedido e guardado em metadados tecnicos.</param>
/// <param name="Sensitive">So com pedido explicito, e nunca em log ou JSONB.</param>
/// <param name="Never">Nunca e pedido nem devolvido, mesmo se o equipamento enviar.</param>
public sealed record DeviceObjectDefinition(
    string Name,
    IReadOnlyList<string> Key,
    IReadOnlyList<string> Safe,
    IReadOnlyList<string> Sensitive,
    IReadOnlyList<string> Never);

public static class ObjectCatalog
{
    public static readonly FrozenDictionary<string, DeviceObjectDefinition> Objects = new[]
    {
        Define("access_events", key: ["id"], safe: ["id", "event", "type", "identification", "device_id", "timestamp"], sensitive: [], never: []),
        Define("access_log_access_rules", key: ["access_log_id", "access_rule_id"], safe: ["access_log_id", "access_rule_id"], sensitive: [], never: []),
        Define("access_logs", key: ["id"], safe: ["id", "time", "event", "device_id", "identifier_id", "user_id", "portal_id", "identification_rule_id", "log_type_id"], sensitive: ["qrcode_value", "uhf_tag", "card_value", "confidence", "mask"], never: ["pin_value"]),
        Define("access_rule_time_zones", key: ["access_rule_id", "time_zone_id"], safe: ["access_rule_id", "time_zone_id"], sensitive: [], never: []),
        Define("access_rules", key: ["id"], safe: ["id", "name", "type", "priority"], sensitive: [], never: []),
        Define("actions", key: ["group_id"], safe: ["group_id", "name", "action", "parameters", "run_at"], sensitive: [], never: []),
        Define("alarm_logs", key: ["id"], safe: ["id", "event", "cause", "user_id", "time", "access_log_id", "door_id"], sensitive: [], never: []),
        Define("alarm_zone_time_zones", key: ["alarm_zone_id", "time_zone_id"], safe: ["alarm_zone_id", "time_zone_id"], sensitive: [], never: []),
        Define("alarm_zones", key: ["zone"], safe: ["zone", "enabled", "active_level", "alarm_delay"], sensitive: [], never: []),
        Define("area_access_rules", key: ["area_id", "access_rule_id"], safe: ["area_id", "access_rule_id"], sensitive: [], never: []),
        Define("areas", key: ["id"], safe: ["id", "name"], sensitive: [], never: []),
        Define("cards", key: ["id"], safe: ["id", "user_id"], sensitive: ["value"], never: []),
        Define("catra_infos", key: ["id"], safe: ["id", "left_turns", "right_turns", "entrance_turns", "exit_turns"], sensitive: [], never: []),
        Define("change_logs", key: ["id"], safe: ["id", "operation_type", "table_name", "table_id", "timestamp"], sensitive: [], never: []),
        Define("contact_contact_groups", key: ["contact_id", "contact_group_id"], safe: ["contact_id", "contact_group_id"], sensitive: [], never: []),
        Define("contact_groups", key: ["id"], safe: ["id", "name", "description", "parent_id"], sensitive: [], never: []),
        Define("contacts", key: ["id"], safe: ["id", "name"], sensitive: ["number", "info"], never: []),
        Define("contingency_card_access_rules", key: ["access_rule_id"], safe: ["access_rule_id"], sensitive: [], never: []),
        Define("contingency_cards", key: ["id"], safe: ["id"], sensitive: ["value"], never: []),
        Define("custom_thresholds", key: ["id"], safe: ["id", "user_id", "threshold"], sensitive: [], never: []),
        Define("devices", key: ["id"], safe: ["id", "name", "ip"], sensitive: [], never: []),
        Define("group_access_rules", key: ["group_id", "access_rule_id"], safe: ["group_id", "access_rule_id"], sensitive: [], never: []),
        Define("groups", key: ["id"], safe: ["id", "name"], sensitive: [], never: []),
        Define("holidays", key: ["id"], safe: ["id", "name", "start", "end", "hol1", "hol2", "hol3", "repeats"], sensitive: [], never: []),
        Define("kiosk_buttons", key: ["id"], safe: ["id", "color", "text", "text_color", "font", "icon_name", "action", "font_size", "icon_width", "icon_height"], sensitive: [], never: []),
        Define("log_types", key: ["id"], safe: ["id", "name"], sensitive: [], never: []),
        Define("network_interlocking_rules", key: ["id"], safe: ["id", "ip", "portal_name", "enabled"], sensitive: [], never: ["login", "password"]),
        Define("pins", key: ["id"], safe: ["id", "user_id"], sensitive: ["value"], never: []),
        Define("portal_access_rules", key: ["portal_id", "access_rule_id"], safe: ["portal_id", "access_rule_id"], sensitive: [], never: []),
        Define("portal_actions", key: ["portal_id", "action_id"], safe: ["portal_id", "action_id"], sensitive: [], never: []),
        Define("portals", key: ["id"], safe: ["id", "name", "area_from_id", "area_to_id"], sensitive: [], never: []),
        Define("qrcodes", key: ["id"], safe: ["id", "user_id"], sensitive: ["value"], never: []),
        Define("scheduled_unlock_access_rules", key: ["scheduled_unlock_id", "access_rule_id"], safe: ["scheduled_unlock_id", "access_rule_id"], sensitive: [], never: []),
        Define("scheduled_unlocks", key: ["id"], safe: ["id", "name", "message"], sensitive: [], never: []),
        Define("sec_boxs", key: ["id"], safe: ["id", "version", "name", "enabled", "relay_timeout", "door_sensor_enabled", "door_sensor_idle", "auto_close_enabled"], sensitive: [], never: []),
        Define("templates", key: ["id"], safe: ["id", "finger_position", "finger_type", "user_id"], sensitive: ["template"], never: []),
        Define("time_spans", key: ["id"], safe: ["id", "time_zone_id", "start", "end", "sun", "mon", "tue", "wed", "thu", "fri", "sat", "hol1", "hol2", "hol3"], sensitive: [], never: []),
        Define("time_zones", key: ["id"], safe: ["id", "name"], sensitive: [], never: []),
        Define("timed_alarms", key: ["id"], safe: ["id", "name", "time", "sun", "mon", "tue", "wed", "thu", "fri", "sat"], sensitive: [], never: []),
        Define("uhf_tags", key: ["id"], safe: ["id", "user_id"], sensitive: ["value"], never: []),
        Define("user_access_rules", key: ["user_id", "access_rule_id"], safe: ["user_id", "access_rule_id"], sensitive: [], never: []),
        Define("user_groups", key: ["user_id", "group_id"], safe: ["user_id", "group_id"], sensitive: [], never: []),
        Define("user_roles", key: ["user_id"], safe: ["user_id", "role"], sensitive: [], never: []),
        Define("users", key: ["id"], safe: ["id", "registration", "name", "user_type_id", "begin_time", "end_time", "image_timestamp", "last_access"], sensitive: ["password", "salt"], never: []),
    }.ToFrozenDictionary(d => d.Name, StringComparer.Ordinal);

    private static DeviceObjectDefinition Define(string name, string[] key, string[] safe, string[] sensitive, string[] never) =>
        new(name, key, safe, sensitive, never);
}
