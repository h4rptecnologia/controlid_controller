using Platform.Shared.Kernel.Authorization;

namespace Platform.UnitTests;

public sealed class PermissionScopeTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();

    private static GrantRow Allow(Guid? site = null) => new(site, PermissionScope.Allow);

    private static GrantRow Deny(Guid? site = null) => new(site, PermissionScope.Deny);

    [Fact]
    public void No_grants_means_no_access()
    {
        var scope = PermissionScope.Evaluate([]);
        Assert.True(scope.IsEmpty);
        Assert.False(scope.Includes(SiteA));
    }

    [Fact]
    public void Tenant_wide_allow_covers_any_site()
    {
        var scope = PermissionScope.Evaluate([Allow()]);
        Assert.True(scope.IsTenantWide);
        Assert.True(scope.Includes(SiteA));
    }

    [Fact]
    public void Site_allow_covers_only_that_site()
    {
        var scope = PermissionScope.Evaluate([Allow(SiteA)]);
        Assert.False(scope.IsTenantWide);
        Assert.True(scope.Includes(SiteA));
        Assert.False(scope.Includes(SiteB));
    }

    [Fact]
    public void Tenant_wide_deny_beats_every_allow()
    {
        Assert.True(PermissionScope.Evaluate([Allow(), Allow(SiteA), Deny()]).IsEmpty);
    }

    [Fact]
    public void Site_deny_beats_tenant_wide_allow_on_that_site_only()
    {
        var scope = PermissionScope.Evaluate([Allow(), Deny(SiteA)]);
        Assert.False(scope.Includes(SiteA));
        Assert.True(scope.Includes(SiteB));
        Assert.False(scope.IsTenantWide);
    }

    [Fact]
    public void Site_deny_beats_same_site_allow()
    {
        Assert.True(PermissionScope.Evaluate([Allow(SiteA), Deny(SiteA)]).IsEmpty);
    }

    [Fact]
    public void Unknown_effect_never_grants()
    {
        Assert.True(PermissionScope.Evaluate([new GrantRow(null, "ALLOW"), new GrantRow(SiteA, "permit")]).IsEmpty);
    }

    [Theory]
    [InlineData(Permissions.DoorUnlock)]
    [InlineData(Permissions.BiometricsRead)]
    [InlineData(Permissions.KeysRotate)]
    [InlineData(Permissions.GrantsSensitive)]
    public void Dangerous_permissions_are_sensitive_and_require_mfa(string name)
    {
        var info = Permissions.Catalog[name];
        Assert.True(info.Sensitive);
        Assert.True(info.RequiresMfa);
    }

    [Fact]
    public void Catalog_has_no_wildcards()
    {
        Assert.DoesNotContain(Permissions.Catalog.Keys, k => k.Contains('*', StringComparison.Ordinal));
    }
}
