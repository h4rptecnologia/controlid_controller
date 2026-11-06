using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Shared.Kernel.Http;

namespace Platform.IntegrationTests;

/// <summary>Esquema so de teste: le a identidade de cabecalhos. Existe apenas neste projeto.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", subject.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant))
        {
            claims.Add(new Claim("tenant", tenant.ToString()));
        }

        if (Request.Headers.ContainsKey("X-Test-Mfa"))
        {
            claims.Add(new Claim("amr", "mfa"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public sealed class HubApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null, string environment = "Testing") : WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Hub", connectionString);
        foreach (var setting in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(setting.Key, setting.Value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                o.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    /// <summary>Envia uma chave de idempotencia nova em cada escrita.</summary>
    public HttpClient ClientFor(TestActor actor, bool mfa = false) =>
        Configure(CreateDefaultClient(new AutoIdempotencyHandler()), actor, mfa);

    /// <summary>Nao envia chave: para os testes da propria idempotencia.</summary>
    public HttpClient PlainClientFor(TestActor actor, bool mfa = false) => Configure(CreateClient(), actor, mfa);

    private static HttpClient Configure(HttpClient client, TestActor actor, bool mfa)
    {
        ArgumentNullException.ThrowIfNull(actor);
        client.DefaultRequestHeaders.Add("X-Test-Sub", actor.Subject);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", actor.TenantSlug);
        client.DefaultRequestHeaders.Add(RequestPipeline.CsrfHeader, "1");
        if (mfa)
        {
            client.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        }

        return client;
    }
}

public sealed class AutoIdempotencyHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && !request.Headers.Contains(Idempotency.KeyHeader))
        {
            request.Headers.Add(Idempotency.KeyHeader, Guid.NewGuid().ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed record TestActor(Guid Id, Guid TenantId, string TenantSlug, string Subject);

/// <summary>Massa criada como superusuario, sem passar pela API testada.</summary>
public sealed class TestData(string adminConnection)
{
    public async Task<(Guid Id, string Slug)> TenantAsync()
    {
        var id = Guid.NewGuid();
        var slug = "t-" + id.ToString("N");
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.ExecuteAsync("INSERT INTO hub.tenants (id, slug, name) VALUES (@id, @slug, @slug)", new { id, slug });
        return (id, slug);
    }

    public async Task<Guid> SiteAsync(Guid tenantId, string name = "Unidade")
    {
        await using var connection = new NpgsqlConnection(adminConnection);
        return await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO hub.sites (tenant_id, name) VALUES (@tenantId, @name) RETURNING id", new { tenantId, name });
    }

    public async Task<TestActor> ActorAsync((Guid Id, string Slug) tenant, params string[] permissions)
    {
        var subject = "sub-" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(adminConnection);
        var id = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO hub.actors (tenant_id, oidc_subject, display_name) VALUES (@tenant, @subject, 'Operador') RETURNING id",
            new { tenant = tenant.Id, subject });
        foreach (var permission in permissions)
        {
            await GrantAsync(new TestActor(id, tenant.Id, tenant.Slug, subject), permission);
        }

        return new TestActor(id, tenant.Id, tenant.Slug, subject);
    }

    public async Task GrantAsync(TestActor actor, string permission, Guid? siteId = null, string effect = "allow")
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.ExecuteAsync(
            "INSERT INTO hub.actor_grants (tenant_id, actor_id, site_id, action_name, effect) VALUES (@TenantId, @Id, @siteId, @permission, @effect)",
            new { actor.TenantId, actor.Id, siteId, permission, effect });
    }

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(adminConnection);
        return await connection.ExecuteScalarAsync<T>(sql, parameters) ?? throw new InvalidOperationException("Sem resultado.");
    }

    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.ExecuteAsync(sql, parameters);
    }
}

internal static class HttpAssertions
{
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
