using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Platform.IntegrationTests;

/// <summary>
/// Confere so o inicio do fluxo, com metadados estaticos. O ciclo completo com um provedor real ainda precisa
/// ser exercitado (docs/entregas/E01.2-identidade.md).
/// </summary>
public sealed class OidcChallengeTests
{
    private static WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker> Factory() =>
        new WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Hub", "Host=127.0.0.1;Port=1;Username=x;Password=x");
            b.UseSetting("Oidc:Authority", "https://idp.example");
            b.UseSetting("Oidc:ClientId", "hub");
            b.UseSetting("Oidc:ClientSecret", "segredo-de-teste");
            b.ConfigureTestServices(services => services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                o => o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                {
                    Issuer = "https://idp.example",
                    AuthorizationEndpoint = "https://idp.example/authorize",
                    TokenEndpoint = "https://idp.example/token",
                })));
        });

    private static HttpClient Client(WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    [Fact]
    public async Task Login_uses_authorization_code_with_pkce()
    {
        await using var factory = Factory();
        var response = await Client(factory).GetAsync(new Uri("/auth/login", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://idp.example/authorize", location, StringComparison.Ordinal);
        Assert.Contains("response_type=code", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", location, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo-de-teste", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_login_availability_without_authentication()
    {
        await using var factory = Factory();
        var configured = await Client(factory).GetStringAsync(new Uri("/auth/status", UriKind.Relative));
        Assert.Contains("\"loginConfigured\":true", configured, StringComparison.Ordinal);
        Assert.DoesNotContain("idp.example", configured, StringComparison.Ordinal);

        await using var bare = new WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Hub", "Host=127.0.0.1;Port=1;Username=x;Password=x");
        });
        var missing = await bare.CreateClient().GetStringAsync(new Uri("/auth/status", UriKind.Relative));
        Assert.Contains("\"loginConfigured\":false", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_answers_401_instead_of_redirecting_to_the_provider()
    {
        await using var factory = Factory();
        var response = await Client(factory).GetAsync(new Uri("/api/v1/sites", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forged_session_cookie_is_ignored()
    {
        await using var factory = Factory();
        var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-hub=forjado");

        var response = await client.GetAsync(new Uri("/api/v1/sites", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
