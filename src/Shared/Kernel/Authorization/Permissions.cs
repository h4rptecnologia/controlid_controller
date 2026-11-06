using System.Collections.Frozen;

namespace Platform.Shared.Kernel.Authorization;

/// <param name="Sensitive">So pode ser concedida por quem tem grants.sensitive, e nunca a si mesmo.</param>
/// <param name="RequiresMfa">Exige sessao autenticada com MFA.</param>
public sealed record PermissionInfo(string Name, bool Sensitive, bool RequiresMfa);

/// <summary>
/// Catalogo fechado de permissoes (docs/06-seguranca.md). A comparacao e sempre exata: nao ha curinga nem
/// heranca, entao door.unlock nunca decorre de device.write e biometrics.read nunca decorre de people.read.
/// </summary>
public static class Permissions
{
    public const string SitesRead = "sites.read";
    public const string SitesWrite = "sites.write";
    public const string ActorsRead = "actors.read";
    public const string ActorsManage = "actors.manage";
    public const string GrantsSensitive = "grants.sensitive";
    public const string PeopleRead = "people.read";
    public const string PeopleWrite = "people.write";
    public const string PeopleRevoke = "people.revoke";
    public const string CredentialsManage = "credentials.manage";
    public const string BiometricsRead = "biometrics.read";
    public const string GatewaysRead = "gateways.read";
    public const string GatewaysManage = "gateways.manage";
    public const string DeviceRead = "device.read";
    public const string DeviceWrite = "device.write";
    public const string DeviceProbe = "device.probe";
    public const string DeviceBackfill = "device.backfill";
    public const string DeviceSettingsWrite = "device.settings.write";
    public const string DoorUnlock = "door.unlock";
    public const string PoliciesRead = "policies.read";
    public const string PoliciesWrite = "policies.write";
    public const string ImportCreate = "import.create";
    public const string ImportApprove = "import.approve";
    public const string SyncRead = "sync.read";
    public const string SyncApprove = "sync.approve";
    public const string EventsRead = "events.read";
    public const string ExportsCreate = "exports.create";
    public const string TenantAuditRead = "tenant.audit.read";
    public const string KeysRotate = "keys.rotate";

    public static readonly FrozenDictionary<string, PermissionInfo> Catalog = new[]
    {
        Plain(SitesRead),
        Plain(SitesWrite),
        Plain(ActorsRead),
        Mfa(ActorsManage),
        Sensitive(GrantsSensitive),
        Plain(PeopleRead),
        Plain(PeopleWrite),
        Plain(PeopleRevoke),
        Mfa(CredentialsManage),
        Sensitive(BiometricsRead),
        Plain(GatewaysRead),
        Mfa(GatewaysManage),
        Plain(DeviceRead),
        Plain(DeviceWrite),
        Plain(DeviceProbe),
        Plain(DeviceBackfill),
        Mfa(DeviceSettingsWrite),
        Sensitive(DoorUnlock),
        Plain(PoliciesRead),
        Mfa(PoliciesWrite),
        Plain(ImportCreate),
        Mfa(ImportApprove),
        Plain(SyncRead),
        Mfa(SyncApprove),
        Plain(EventsRead),
        Mfa(ExportsCreate),
        Plain(TenantAuditRead),
        Sensitive(KeysRotate),
    }.ToFrozenDictionary(p => p.Name, StringComparer.Ordinal);

    private static PermissionInfo Plain(string name) => new(name, Sensitive: false, RequiresMfa: false);

    private static PermissionInfo Mfa(string name) => new(name, Sensitive: false, RequiresMfa: true);

    private static PermissionInfo Sensitive(string name) => new(name, Sensitive: true, RequiresMfa: true);
}
