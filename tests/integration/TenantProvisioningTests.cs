using System.Net;
using Platform.Migrator;
using Platform.Shared.Kernel.Authorization;

namespace Platform.IntegrationTests;

[Collection(PostgresTests.Name)]
public sealed class TenantProvisioningTests(PostgresFixture fixture) : IDisposable
{
    private readonly TestData data = new(fixture.AdminOnHubConnection);
    private readonly HubApiFactory factory = new(fixture.AppConnection);

    public void Dispose() => factory.Dispose();

    [Fact]
    public void First_admin_never_starts_with_physical_biometric_or_key_permissions()
    {
        var initial = TenantProvisioner.InitialPermissions();

        Assert.DoesNotContain(Permissions.DoorUnlock, initial);
        Assert.DoesNotContain(Permissions.BiometricsRead, initial);
        Assert.DoesNotContain(Permissions.KeysRotate, initial);
        Assert.Contains(Permissions.GrantsSensitive, initial);
    }

    [Fact]
    public async Task Provisioned_admin_can_use_the_api_inside_own_tenant()
    {
        var slug = "prov-" + Guid.NewGuid().ToString("N");
        var subject = "admin-" + Guid.NewGuid().ToString("N");

        var tenantId = await TenantProvisioner.RunAsync(fixture.AdminOnHubConnection, slug, "Cliente", subject, "Admin");

        var actorId = await data.ScalarAsync<Guid>("SELECT id FROM hub.actors WHERE tenant_id = @tenantId", new { tenantId });
        var client = factory.ClientFor(new TestActor(actorId, tenantId, slug, subject), mfa: true);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/sites", UriKind.Relative))).StatusCode);
        Assert.Equal(1L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.audit_log WHERE tenant_id = @tenantId AND action = 'tenant.provision'", new { tenantId }));
    }
}
