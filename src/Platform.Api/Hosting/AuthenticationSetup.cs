using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Platform.Modules.Fleet;

namespace Platform.Api.Hosting;

internal static class AuthenticationSetup
{
    /// <summary>
    /// BFF: o browser so guarda um cookie HttpOnly; nenhum token chega ao JavaScript.
    /// Sem Oidc:Authority configurado a API continua fechada, em vez de cair em algum modo aberto.
    /// </summary>
    public static IServiceCollection AddHubAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var authority = configuration["Oidc:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            services
                .AddAuthentication(DenyAllAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DenyAllAuthenticationHandler>(DenyAllAuthenticationHandler.SchemeName, null)
                .AddEdgeGatewayScheme();
            return services;
        }

        var authentication = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "__Host-hub";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax; // Strict derrubaria o retorno do provedor OIDC.
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;

                // API responde com status, sem redirecionar para pagina de login.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = authority;
                options.ClientId = configuration["Oidc:ClientId"];
                options.ClientSecret = configuration["Oidc:ClientSecret"];
                options.ResponseType = "code";
                options.UsePkce = true;
                options.CallbackPath = "/auth/callback";
                options.SignedOutCallbackPath = "/auth/signed-out";
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.TokenValidationParameters.NameClaimType = "name";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");

                // Necessarios para exigir MFA e para a checagem de login recente.
                options.ClaimActions.Remove("amr");
                options.ClaimActions.Remove("auth_time");
            });
        authentication.AddEdgeGatewayScheme();

        return services;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Permite a tela de entrada explicar a falta de provedor em vez de levar a um erro.
        routes.MapGet("/auth/status", (HttpContext http) => Results.Ok(new { loginConfigured = HasOidc(http) }))
            .AllowAnonymous()
            .ExcludeFromDescription();

        routes.MapGet("/auth/login", (string? returnUrl, HttpContext http) =>
            {
                // So caminho local: evita redirecionamento aberto apos o login.
                var target = returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.Contains('\\', StringComparison.Ordinal)
                    ? returnUrl
                    : "/";
                return HasOidc(http)
                    ? Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        routes.MapPost("/auth/logout", (HttpContext http) =>
                HasOidc(http)
                    ? Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.NoContent())
            .ExcludeFromDescription();

        return routes;
    }

    private static bool HasOidc(HttpContext http) =>
        !string.IsNullOrWhiteSpace(http.RequestServices.GetRequiredService<IConfiguration>()["Oidc:Authority"]);
}
