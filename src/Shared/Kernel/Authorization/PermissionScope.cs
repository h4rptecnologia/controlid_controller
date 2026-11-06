namespace Platform.Shared.Kernel.Authorization;

public sealed record GrantRow(Guid? SiteId, string Effect);

/// <summary>Alcance efetivo de uma permissao para um ator. Negacao sempre prevalece (ADR-009).</summary>
public sealed class PermissionScope
{
    public const string Allow = "allow";
    public const string Deny = "deny";

    public static readonly PermissionScope None = new(false, new HashSet<Guid>(), new HashSet<Guid>());

    private PermissionScope(bool allSites, IReadOnlySet<Guid> allowedSites, IReadOnlySet<Guid> deniedSites)
    {
        AllSites = allSites;
        AllowedSites = allowedSites;
        DeniedSites = deniedSites;
    }

    public bool AllSites { get; }

    public IReadOnlySet<Guid> AllowedSites { get; }

    public IReadOnlySet<Guid> DeniedSites { get; }

    public bool IsEmpty => !AllSites && AllowedSites.Count == 0;

    /// <summary>Verdadeiro so quando vale para o tenant inteiro, sem nenhuma unidade negada.</summary>
    public bool IsTenantWide => AllSites && DeniedSites.Count == 0;

    public bool Includes(Guid siteId) =>
        !DeniedSites.Contains(siteId) && (AllSites || AllowedSites.Contains(siteId));

    public static PermissionScope Evaluate(IEnumerable<GrantRow> grants)
    {
        var rows = grants.ToList();
        if (rows.Any(g => g.Effect == Deny && g.SiteId is null))
        {
            return None;
        }

        // Efeito desconhecido nunca concede: so "allow" literal conta.
        var denied = rows.Where(g => g.Effect == Deny).Select(g => g.SiteId!.Value).ToHashSet();
        var allowed = rows.Where(g => g.Effect == Allow).ToList();
        var allSites = allowed.Any(g => g.SiteId is null);
        var sites = allowed.Where(g => g.SiteId is not null).Select(g => g.SiteId!.Value).Where(s => !denied.Contains(s)).ToHashSet();

        return allSites || sites.Count > 0 ? new PermissionScope(allSites, sites, denied) : None;
    }
}
