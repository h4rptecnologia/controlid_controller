using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Platform.IntegrationTests;

[Collection(PostgresTests.Name)]
public sealed class ApiHostTests(PostgresFixture fixture)
{
    private static WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker> Factory(string connectionString) =>
        new WebApplicationFactory<Platform.Api.Hosting.ApiAssemblyMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Hub", connectionString);
        });

    [Fact]
    public async Task Liveness_is_anonymous()
    {
        await using var factory = Factory(fixture.AppConnection);
        var response = await factory.CreateClient().GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_ok_with_real_database()
    {
        await using var factory = Factory(fixture.AppConnection);
        var response = await factory.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_without_leaking_connection_details()
    {
        await using var factory = Factory("Host=127.0.0.1;Port=1;Username=segredo_usuario;Password=segredo_senha;Timeout=1");
        var response = await factory.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("segredo", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/devices")]
    [InlineData("/qualquer-rota")]
    public async Task Everything_else_requires_authentication(string path)
    {
        await using var factory = Factory(fixture.AppConnection);
        var response = await factory.CreateClient().GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
