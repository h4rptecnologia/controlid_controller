using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Http;

namespace Platform.IntegrationTests;

[Collection(PostgresTests.Name)]
public sealed class IdempotencyTests(PostgresFixture fixture) : IDisposable
{
    private readonly TestData data = new(fixture.AdminOnHubConnection);
    private readonly HubApiFactory factory = new(fixture.AppConnection);

    public void Dispose() => factory.Dispose();

    private static readonly Uri Sites = new("/api/v1/sites", UriKind.Relative);

    private static HttpRequestMessage Post(string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Sites) { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.KeyHeader, key);
        return request;
    }

    private Task<long> SitesOfAsync(Guid tenant) =>
        data.ScalarAsync<long>("SELECT count(*) FROM hub.sites WHERE tenant_id = @tenant", new { tenant });

    [Fact]
    public async Task Missing_or_invalid_key_is_rejected_without_executing()
    {
        var tenant = await data.TenantAsync();
        var client = factory.PlainClientFor(await data.ActorAsync(tenant, Permissions.SitesWrite));

        var missing = await client.PostAsJsonAsync(Sites, new { name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("idempotency_key_required", await missing.ProblemCodeAsync());

        var tooLong = await client.SendAsync(Post(new string('k', 129), new { name = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Equal(0, await SitesOfAsync(tenant.Id));
    }

    [Fact]
    public async Task Repeating_a_request_returns_the_original_response_without_executing_again()
    {
        var tenant = await data.TenantAsync();
        var client = factory.PlainClientFor(await data.ActorAsync(tenant, Permissions.SitesWrite));
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(Post(key, new { name = "Matriz" }));
        var second = await client.SendAsync(Post(key, new { name = "Matriz" }));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains(Idempotency.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(Idempotency.ReplayedHeader).Single());
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal("application/json", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, await SitesOfAsync(tenant.Id));
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_a_conflict()
    {
        var tenant = await data.TenantAsync();
        var client = factory.PlainClientFor(await data.ActorAsync(tenant, Permissions.SitesWrite));
        var key = Guid.NewGuid().ToString();

        await client.SendAsync(Post(key, new { name = "A" }));
        var response = await client.SendAsync(Post(key, new { name = "B" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_key_reused", await response.ProblemCodeAsync());
        Assert.Equal(1, await SitesOfAsync(tenant.Id));
    }

    [Fact]
    public async Task Simultaneous_requests_with_same_key_execute_once()
    {
        var tenant = await data.TenantAsync();
        var client = factory.PlainClientFor(await data.ActorAsync(tenant, Permissions.SitesWrite));
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.SendAsync(Post(key, new { name = "Paralela" }))));

        Assert.Equal(1, await SitesOfAsync(tenant.Id));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains(Idempotency.ReplayedHeader));
    }

    [Fact]
    public async Task Validation_failure_releases_the_key_for_a_corrected_retry_of_the_same_request()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesWrite);
        var client = factory.PlainClientFor(actor);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post(key, new { name = " " }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Post(key, new { name = "Corrigida" }))).StatusCode);
    }

    [Fact]
    public async Task Key_is_scoped_by_actor_and_tenant()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var key = Guid.NewGuid().ToString();
        var actor1 = factory.PlainClientFor(await data.ActorAsync(a, Permissions.SitesWrite));
        var actor2 = factory.PlainClientFor(await data.ActorAsync(a, Permissions.SitesWrite));
        var actor3 = factory.PlainClientFor(await data.ActorAsync(b, Permissions.SitesWrite));

        Assert.Equal(HttpStatusCode.Created, (await actor1.SendAsync(Post(key, new { name = "Um" }))).StatusCode);

        var second = await actor2.SendAsync(Post(key, new { name = "Um" }));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(second.Headers.Contains(Idempotency.ReplayedHeader));

        var third = await actor3.SendAsync(Post(key, new { name = "Um" }));
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.False(third.Headers.Contains(Idempotency.ReplayedHeader));

        Assert.Equal(2, await SitesOfAsync(a.Id));
        Assert.Equal(1, await SitesOfAsync(b.Id));
    }

    [Fact]
    public async Task Expired_key_can_be_used_again()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesWrite);
        var client = factory.PlainClientFor(actor);
        var key = Guid.NewGuid().ToString();

        await client.SendAsync(Post(key, new { name = "A" }));
        await data.ExecuteAsync("UPDATE hub.idempotency_keys SET expires_at = now() - interval '1 minute' WHERE actor_id = @Id", new { actor.Id });

        var again = await client.SendAsync(Post(key, new { name = "B" }));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.False(again.Headers.Contains(Idempotency.ReplayedHeader));
        Assert.Equal(2, await SitesOfAsync(tenant.Id));
    }

    [Fact]
    public async Task Key_stuck_in_progress_is_never_executed_again()
    {
        // O processo morreu depois da reserva: nao se sabe se o efeito ocorreu.
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesWrite);
        var client = factory.PlainClientFor(actor);
        var key = Guid.NewGuid().ToString();
        await client.SendAsync(Post(key, new { name = "A" }));
        await data.ExecuteAsync("UPDATE hub.idempotency_keys SET state = 'in_progress', response_status = NULL WHERE actor_id = @Id", new { actor.Id });

        var response = await client.SendAsync(Post(key, new { name = "A" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("request_in_progress", await response.ProblemCodeAsync());
        Assert.Equal(1, await SitesOfAsync(tenant.Id));
    }

    [Fact]
    public void Every_write_route_is_idempotent_or_explicitly_exempt()
    {
        // Rewrap e reentrante por natureza: processa o que falta e nao tem efeito novo ao repetir.
        // Token e senha nao podem ficar na tabela de idempotencia, nem como hash no caso da senha.
        string[] exempt =
        [
            "/api/v1/keys/{purpose}/rewrap",
            "/api/v1/imports/{id:guid}/content",
            "/api/v1/imports/{id:guid}/unlock",
            "/api/v1/gateways/",
            "/api/v1/gateways/{id:guid}/enrollment-token",
            "/api/v1/devices/{id:guid}/credential",
            "/api/v1/people/{id:guid}/credentials",
            "/api/v1/credentials/lookup",
        ];
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m != "GET") == true)
            .ToList();

        Assert.True(routes.Count > 0);
        Assert.Empty(routes
            .Where(e => e.Metadata.GetMetadata<IdempotentMetadata>() is null && !exempt.Contains(e.RoutePattern.RawText))
            .Select(e => e.RoutePattern.RawText));
    }
}

[Collection(PostgresTests.Name)]
public sealed class ErrorContractTests(PostgresFixture fixture) : IDisposable
{
    private readonly TestData data = new(fixture.AdminOnHubConnection);
    private readonly HubApiFactory factory = new(fixture.AppConnection);

    public void Dispose() => factory.Dispose();

    private static Uri Url(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task Malformed_json_returns_problem_with_stable_code()
    {
        var tenant = await data.TenantAsync();
        var client = factory.ClientFor(await data.ActorAsync(tenant, Permissions.SitesWrite));

        var response = await client.PostAsync(Url("/api/v1/sites"), new StringContent("{nao e json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("invalid_input", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Unknown_fields_are_rejected_including_a_forged_tenant()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var client = factory.ClientFor(await data.ActorAsync(a, Permissions.SitesWrite));

        var response = await client.PostAsJsonAsync(Url("/api/v1/sites"), new { name = "x", tenantId = b.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.sites WHERE tenant_id = ANY(@ids)", new { ids = new[] { a.Id, b.Id } }));
    }

    [Fact]
    public async Task Database_failure_does_not_expose_internals()
    {
        var tenant = await data.TenantAsync();
        var actor = await data.ActorAsync(tenant, Permissions.SitesRead);
        using var broken = new HubApiFactory("Host=127.0.0.1;Port=1;Username=usuario_interno;Password=senha_interna;Timeout=1");

        var response = await broken.ClientFor(actor).GetAsync(Url("/api/v1/sites"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("usuario_interno", body, StringComparison.Ordinal);
        Assert.DoesNotContain("senha_interna", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/sites")]
    [InlineData("/health/live")]
    public async Task Security_headers_are_present(string path)
    {
        var response = await factory.CreateClient().GetAsync(Url(path));

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.StartsWith("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_responses_are_not_cacheable()
    {
        var tenant = await data.TenantAsync();
        var client = factory.ClientFor(await data.ActorAsync(tenant, Permissions.SitesRead));

        var response = await client.GetAsync(Url("/api/v1/sites"));

        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}

[Collection(PostgresTests.Name)]
public sealed class RateLimitTests(PostgresFixture fixture)
{
    private readonly TestData data = new(fixture.AdminOnHubConnection);

    private HubApiFactory Limited(int perMinute, int sensitive = 100) => new(fixture.AppConnection, new Dictionary<string, string?>
    {
        ["RateLimit:PerMinute"] = perMinute.ToString(CultureInfo.InvariantCulture),
        ["RateLimit:SensitivePerMinute"] = sensitive.ToString(CultureInfo.InvariantCulture),
    });

    [Fact]
    public async Task Exceeding_the_limit_returns_429_and_only_affects_that_actor()
    {
        using var factory = Limited(2);
        var tenant = await data.TenantAsync();
        var noisy = factory.ClientFor(await data.ActorAsync(tenant, Permissions.SitesRead));
        var quiet = factory.ClientFor(await data.ActorAsync(tenant, Permissions.SitesRead));
        var url = new Uri("/api/v1/sites", UriKind.Relative);

        await noisy.GetAsync(url);
        await noisy.GetAsync(url);
        var limited = await noisy.GetAsync(url);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await limited.ProblemCodeAsync());
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await quiet.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Health_is_not_limited()
    {
        using var factory = Limited(1);
        var client = factory.CreateClient();
        var url = new Uri("/health/live", UriKind.Relative);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Sensitive_routes_have_a_lower_limit()
    {
        using var factory = Limited(100, sensitive: 1);
        var tenant = await data.TenantAsync();
        var admin = await data.ActorAsync(tenant, Permissions.ActorsManage);
        var target = await data.ActorAsync(tenant);
        var client = factory.ClientFor(admin, mfa: true);
        var url = new Uri($"/api/v1/actors/{target.Id}/grants", UriKind.Relative);

        await client.PostAsJsonAsync(url, new { action = Permissions.ActorsManage });
        var limited = await client.PostAsJsonAsync(url, new { action = Permissions.SitesRead });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}

[Collection(PostgresTests.Name)]
public sealed class AuditQueryTests(PostgresFixture fixture) : IDisposable
{
    private readonly TestData data = new(fixture.AdminOnHubConnection);
    private readonly HubApiFactory factory = new(fixture.AppConnection);

    public void Dispose() => factory.Dispose();

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<TestActor> SeedAsync((Guid Id, string Slug) tenant, int entries)
    {
        var actor = await data.ActorAsync(tenant, Permissions.TenantAuditRead);
        for (var i = 0; i < entries; i++)
        {
            await data.ExecuteAsync(
                "INSERT INTO hub.audit_log (tenant_id, actor_id, action, resource_type, outcome) VALUES (@TenantId, @Id, @action, 'teste', @outcome)",
                new { actor.TenantId, actor.Id, action = "acao." + i, outcome = i % 2 == 0 ? "success" : "denied" });
        }

        return actor;
    }

    [Fact]
    public async Task Requires_permission_and_stays_in_own_tenant()
    {
        var a = await data.TenantAsync();
        var b = await data.TenantAsync();
        var other = await SeedAsync(b, 3);
        var reader = await SeedAsync(a, 2);

        var deniedActor = await data.ActorAsync(a, Permissions.SitesRead);
        var denied = await factory.ClientFor(deniedActor).GetAsync(Url("/api/v1/audit"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var body = await (await factory.ClientFor(reader).GetAsync(Url("/api/v1/audit?limit=200"))).Content.ReadFromJsonAsync<JsonElement>();
        var actors = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("actorId").GetGuid()).ToList();

        // As 2 entradas semeadas mais a negacao acima, que tambem e auditada. Nada do outro tenant.
        Assert.Equal(3, actors.Count);
        Assert.Equal(2, actors.Count(id => id == reader.Id));
        Assert.Equal(1, actors.Count(id => id == deniedActor.Id));
        Assert.DoesNotContain(other.Id, actors);
    }

    [Fact]
    public async Task Entries_show_who_acted()
    {
        var tenant = await data.TenantAsync();
        var client = factory.ClientFor(await SeedAsync(tenant, 1));

        var body = await (await client.GetAsync(Url("/api/v1/audit"))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Operador", body.GetProperty("items")[0].GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task Site_scoped_auditor_is_refused()
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var auditor = await data.ActorAsync(tenant);
        await data.GrantAsync(auditor, Permissions.TenantAuditRead, site);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(auditor).GetAsync(Url("/api/v1/audit"))).StatusCode);
    }

    [Fact]
    public async Task Cursor_walks_every_entry_exactly_once()
    {
        var tenant = await data.TenantAsync();
        var client = factory.ClientFor(await SeedAsync(tenant, 7));
        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var path = "/api/v1/audit?limit=3" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await (await client.GetAsync(Url(path))).Content.ReadFromJsonAsync<JsonElement>();
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Filters_apply()
    {
        var tenant = await data.TenantAsync();
        var client = factory.ClientFor(await SeedAsync(tenant, 4));

        var denied = await (await client.GetAsync(Url("/api/v1/audit?outcome=denied"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, denied.GetProperty("items").GetArrayLength());

        var action = await (await client.GetAsync(Url("/api/v1/audit?action=acao.0"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, action.GetProperty("items").GetArrayLength());

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture));
        var none = await (await client.GetAsync(Url($"/api/v1/audit?from={future}"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("cursor=nao-e-base64!")]
    [InlineData("cursor=YWJj")]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    public async Task Invalid_query_is_rejected(string query)
    {
        var tenant = await data.TenantAsync();
        var client = factory.ClientFor(await data.ActorAsync(tenant, Permissions.TenantAuditRead));

        var response = await client.GetAsync(Url("/api/v1/audit?" + query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class OpenApiContractTests
{
    private static readonly string Snapshot = Path.Combine(FindRoot(), "docs", "api", "openapi.v1.json");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ControlIdHub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositorio nao encontrada.");
    }

    private static async Task<string> ReadDocumentAsync(string environment)
    {
        using var factory = new HubApiFactory("Host=127.0.0.1;Port=1;Username=x;Password=x", environment: environment);
        var response = await factory.CreateClient().GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : string.Empty;
    }

    [Fact]
    public async Task Document_matches_the_versioned_contract()
    {
        var document = (await ReadDocumentAsync("Development")).ReplaceLineEndings("\n").Trim() + "\n";
        Assert.StartsWith("{", document, StringComparison.Ordinal);

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            await File.WriteAllTextAsync(Snapshot, document);
        }

        // Mudou a API? Revise a diferenca e regenere com UPDATE_OPENAPI_SNAPSHOT=1.
        Assert.Equal((await File.ReadAllTextAsync(Snapshot)).ReplaceLineEndings("\n"), document);
    }

    [Fact]
    public async Task Document_is_openapi_31_and_only_exposes_the_platform_api()
    {
        using var document = JsonDocument.Parse(await ReadDocumentAsync("Development"));

        Assert.StartsWith("3.1", document.RootElement.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/api/v1/sites", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/", p, StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("fcgi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Document_is_not_published_outside_development()
    {
        Assert.Equal(string.Empty, await ReadDocumentAsync("Testing"));
    }
}
