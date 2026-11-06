using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Platform.Modules.Fleet;

public static class EdgeIdentity
{
    public const string Scheme = "EdgeGateway";
    public const string TenantClaim = "hub_edge_tenant";
    public const string GatewayClaim = "hub_edge_gateway";

    /// <summary>
    /// Identidade = SHA-256 da chave publica. Vem do handshake TLS, que prova a posse da chave privada,
    /// e sobrevive a renovacao do certificado quando a chave e mantida.
    /// </summary>
    public static string ThumbprintOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public static bool IsWithinValidity(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var now = DateTime.Now;
        return certificate.NotBefore <= now && now <= certificate.NotAfter;
    }

    public static Guid TenantOf(this HttpContext http) => Guid.Parse(http.User.FindFirstValue(TenantClaim)!);

    public static Guid GatewayOf(this HttpContext http) => Guid.Parse(http.User.FindFirstValue(GatewayClaim)!);
}

/// <summary>Autentica pelo certificado de cliente. Sem certificado, ou com chave desconhecida ou revogada, nao ha identidade.</summary>
internal sealed class EdgeGatewayAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NpgsqlDataSource dataSource)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var certificate = await Context.Connection.GetClientCertificateAsync(Context.RequestAborted);
        if (certificate is null || !EdgeIdentity.IsWithinValidity(certificate))
        {
            return AuthenticateResult.NoResult();
        }

        await using var connection = await dataSource.OpenConnectionAsync(Context.RequestAborted);
        var gateway = await connection.QuerySingleOrDefaultAsync<ResolvedGateway>(
            "SELECT tenant_id AS TenantId, gateway_id AS GatewayId FROM hub.resolve_gateway(@thumbprint)",
            new { thumbprint = EdgeIdentity.ThumbprintOf(certificate) });

        if (gateway is null)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(
            [new Claim(EdgeIdentity.TenantClaim, gateway.TenantId.ToString()), new Claim(EdgeIdentity.GatewayClaim, gateway.GatewayId.ToString())],
            EdgeIdentity.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), EdgeIdentity.Scheme));
    }

    private sealed record ResolvedGateway(Guid TenantId, Guid GatewayId);
}

public static class EdgeAuthenticationSetup
{
    public static void AddEdgeGatewayScheme(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddScheme<AuthenticationSchemeOptions, EdgeGatewayAuthenticationHandler>(EdgeIdentity.Scheme, null);
    }
}
