using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Http;

namespace Platform.IntegrationTests;

[Collection(PostgresTests.Name)]
public sealed class AuthorizationTests(PostgresFixture fixture) : IDisposable
{
    private readonly TestData data = new(fixture.AdminOnHubConnection);
    private readonly HubApiFactory factory = new(fixture.AppConnection);

    public void Dispose() => factory.Dispose();

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public void Every_api_route_declares_authorization()
    {
        var apiRoutes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .ToList();

        Assert.True(apiRoutes.Count > 0, "Nenhuma rota /api encontrada: o teste nao estaria verificando nada.");
        Assert.Empty(apiRoutes.Where(e => e.Metadata.GetMetadata<PermissionMetadata>() is null).Select(e => e.RoutePattern.RawText));
    }

    [Fact]
    public async Task Tenant_comes_from_the_token_so_other_tenant_site_is_not_found()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var siteA = await data.SiteAsync(a.Id);
        var siteB = await data.SiteAsync(b.Id);
        var actor = await data.ActorAsync(a, Permissions.SitesRead);
        var client = factory.ClientFor(actor);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url($"/api/v1/sites/{siteA}"))).StatusCode);

        var other = await client.GetAsync(Url($"/api/v1/sites/{siteB}"));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("not_found_or_not_in_scope", await other.ProblemCodeAsync());

        Assert.Equal([siteA], await IdsAsync(await client.GetAsync(Url("/api/v1/sites"))));
    }

    [Fact]
    public async Task Subject_of_another_tenant_is_rejected()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var actor = await data.ActorAsync(a, Permissions.SitesRead);
        var spoofed = factory.ClientFor(actor with { TenantSlug = b.Slug });

        var response = await spoofed.GetAsync(Url("/api/v1/sites"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no_active_actor", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Disabled_actor_and_suspended_tenant_are_rejected()
    {
        var tenant = await data.TenantAsync();
        var disabled = await data.ActorAsync(tenant, Permissions.SitesRead);
        await data.ExecuteAsync("UPDATE hub.actors SET disabled_at = now() WHERE id = @Id", new { disabled.Id });
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(disabled).GetAsync(Url("/api/v1/sites"))).StatusCode);

        var suspended = await data.TenantAsync();
        var actor = await data.ActorAsync(suspended, Permissions.SitesRead);
        await data.ExecuteAsync("UPDATE hub.tenants SET status = 'suspended' WHERE id = @Id", new { suspended.Id });
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(actor).GetAsync(Url("/api/v1/sites"))).StatusCode);
    }

    [Fact]
    public async Task Missing_permission_is_denied_and_audited()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant);
        var client = factory.ClientFor(actor);

        var response = await client.GetAsync(Url("/api/v1/sites"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await response.ProblemCodeAsync());
        Assert.Equal(1, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.audit_log WHERE actor_id = @Id AND outcome = 'denied' AND action = 'sites.read'", new { actor.Id }));
    }

    [Fact]
    public async Task Site_scope_limits_results_and_deny_prevails()
    {
        var tenant = await data.TenantAsync();
        var site1 = await data.SiteAsync(tenant.Id, "A");
        var site2 = await data.SiteAsync(tenant.Id, "B");

        var scoped = await data.ActorAsync(tenant);
        await data.GrantAsync(scoped, Permissions.SitesRead, site1);
        var client = factory.ClientFor(scoped);
        Assert.Equal([site1], await IdsAsync(await client.GetAsync(Url("/api/v1/sites"))));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url($"/api/v1/sites/{site2}"))).StatusCode);

        var denied = await data.ActorAsync(tenant, Permissions.SitesRead);
        await data.GrantAsync(denied, Permissions.SitesRead, site1, PermissionScope.Deny);
        var deniedClient = factory.ClientFor(denied);
        Assert.Equal([site2], await IdsAsync(await deniedClient.GetAsync(Url("/api/v1/sites"))));
        Assert.Equal(HttpStatusCode.NotFound, (await deniedClient.GetAsync(Url($"/api/v1/sites/{site1}"))).StatusCode);
    }

    [Fact]
    public async Task Mfa_permission_requires_second_factor()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.ActorsManage);
        var body = new { oidcSubject = "novo-" + Guid.NewGuid().ToString("N"), displayName = "Novo" };

        var withoutMfa = await factory.ClientFor(actor).PostAsJsonAsync(Url("/api/v1/actors"), body);
        Assert.Equal(HttpStatusCode.Forbidden, withoutMfa.StatusCode);
        Assert.Equal("mfa_required", await withoutMfa.ProblemCodeAsync());

        var withMfa = await factory.ClientFor(actor, mfa: true).PostAsJsonAsync(Url("/api/v1/actors"), body);
        Assert.Equal(HttpStatusCode.Created, withMfa.StatusCode);
    }

    [Fact]
    public async Task Write_without_csrf_header_or_from_other_origin_is_refused()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesWrite);

        var noHeader = factory.ClientFor(actor);
        noHeader.DefaultRequestHeaders.Remove(RequestPipeline.CsrfHeader);
        var refused = await noHeader.PostAsJsonAsync(Url("/api/v1/sites"), new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("csrf_failed", await refused.ProblemCodeAsync());

        var otherOrigin = factory.ClientFor(actor);
        otherOrigin.DefaultRequestHeaders.Add("Origin", "https://malicioso.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await otherOrigin.PostAsJsonAsync(Url("/api/v1/sites"), new { name = "x" })).StatusCode);

        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.sites WHERE tenant_id = @Id", new { tenant.Id }));
    }

    [Fact]
    public async Task Creating_site_validates_input_and_writes_audit_in_own_tenant()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesWrite);
        var client = factory.ClientFor(actor);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Url("/api/v1/sites"), new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Url("/api/v1/sites"), new { name = "x", timezone = "Lua/Base" })).StatusCode);

        var created = await client.PostAsJsonAsync(Url("/api/v1/sites"), new { name = "Matriz" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.Equal(1L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.sites WHERE tenant_id = @Id", new { tenant.Id }));
        Assert.Equal(1L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.audit_log WHERE actor_id = @Id AND action = 'sites.write' AND outcome = 'success'", new { actor.Id }));
    }

    [Fact]
    public async Task Site_scoped_writer_cannot_create_new_site()
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var actor = await data.ActorAsync(tenant);
        await data.GrantAsync(actor, Permissions.SitesWrite, site);

        var response = await factory.ClientFor(actor).PostAsJsonAsync(Url("/api/v1/sites"), new { name = "Nova" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Nobody_changes_their_own_permissions_or_disables_themselves()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.ActorsManage, Permissions.GrantsSensitive);
        var client = factory.ClientFor(actor, mfa: true);

        var grant = await client.PostAsJsonAsync(Url($"/api/v1/actors/{actor.Id}/grants"), new { action = Permissions.DoorUnlock });
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
        Assert.Equal("self_change_forbidden", await grant.ProblemCodeAsync());

        var disable = await client.PostAsync(Url($"/api/v1/actors/{actor.Id}/disable"), null);
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
    }

    [Fact]
    public async Task Sensitive_permission_needs_specific_authorization_from_someone_else()
    {
        var tenant = await data.TenantAsync();
        var target = await data.ActorAsync(tenant);

        var plainAdmin = await data.ActorAsync(tenant, Permissions.ActorsManage);
        var refused = await factory.ClientFor(plainAdmin, mfa: true)
            .PostAsJsonAsync(Url($"/api/v1/actors/{target.Id}/grants"), new { action = Permissions.DoorUnlock });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("sensitive_grant_forbidden", await refused.ProblemCodeAsync());

        var authorized = await data.ActorAsync(tenant, Permissions.ActorsManage, Permissions.GrantsSensitive);
        var accepted = await factory.ClientFor(authorized, mfa: true)
            .PostAsJsonAsync(Url($"/api/v1/actors/{target.Id}/grants"), new { action = Permissions.DoorUnlock });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task Cannot_grant_access_you_do_not_hold()
    {
        var tenant = await data.TenantAsync();
        var target = await data.ActorAsync(tenant);
        var admin = await data.ActorAsync(tenant, Permissions.ActorsManage);

        var response = await factory.ClientFor(admin, mfa: true)
            .PostAsJsonAsync(Url($"/api/v1/actors/{target.Id}/grants"), new { action = Permissions.PeopleWrite });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("grant_exceeds_own_access", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Grants_reject_unknown_permission_and_targets_of_other_tenants()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var admin = await data.ActorAsync(a, Permissions.ActorsManage, Permissions.PeopleRead);
        var localTarget = await data.ActorAsync(a);
        var foreignTarget = await data.ActorAsync(b);
        var foreignSite = await data.SiteAsync(b.Id);
        var client = factory.ClientFor(admin, mfa: true);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            Url($"/api/v1/actors/{localTarget.Id}/grants"), new { action = "*" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(
            Url($"/api/v1/actors/{foreignTarget.Id}/grants"), new { action = Permissions.PeopleRead })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(
            Url($"/api/v1/actors/{foreignTarget.Id}/disable"), null)).StatusCode);

        // Escopo em unidade de outro tenant nao e coberto pelo proprio acesso? O alvo local recebe 404, nunca grava.
        var foreignSiteGrant = await client.PostAsJsonAsync(
            Url($"/api/v1/actors/{localTarget.Id}/grants"), new { action = Permissions.PeopleRead, siteId = foreignSite });
        Assert.Equal(HttpStatusCode.NotFound, foreignSiteGrant.StatusCode);
    }

    [Fact]
    public async Task Me_returns_own_grants_only()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesRead);
        await data.ActorAsync(tenant, Permissions.PeopleRead);

        var body = await (await factory.ClientFor(actor).GetAsync(Url("/api/v1/me"))).Content.ReadFromJsonAsync<JsonElement>();

        var grant = Assert.Single(body.GetProperty("grants").EnumerateArray());
        Assert.Equal(Permissions.SitesRead, grant.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Errors_are_problem_json_with_correlation_id()
    {
        var correlation = Guid.NewGuid();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestPipeline.CorrelationHeader, correlation.ToString());

        var response = await client.GetAsync(Url("/api/v1/sites"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(correlation.ToString(), response.Headers.GetValues(RequestPipeline.CorrelationHeader).Single());
    }

    [Fact]
    public async Task Invalid_correlation_header_is_replaced()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(RequestPipeline.CorrelationHeader, "<script>");

        var response = await client.GetAsync(Url("/health/live"));

        Assert.True(Guid.TryParse(response.Headers.GetValues(RequestPipeline.CorrelationHeader).Single(), out _));
    }
}
