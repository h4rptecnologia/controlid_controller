using System.Security.Claims;
using System.Threading.RateLimiting;
using Platform.Shared.Kernel.Http;

namespace Platform.Api.Hosting;

internal static class ApiProtection
{
    public const string SensitivePolicy = "sensitive";

    /// <summary>
    /// Limites por operador e tenant, ou por IP se anonimo. O contador fica na memoria do processo: com varias
    /// instancias o limite efetivo se multiplica. O IP real depende de configurar o proxy (ForwardedHeaders).
    /// </summary>
    public static IServiceCollection AddHubRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var general = configuration.GetValue("RateLimit:PerMinute", 300);
        var sensitive = configuration.GetValue("RateLimit:SensitivePerMinute", 20);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
                    ? RateLimitPartition.GetNoLimiter("health")
                    : FixedWindow(PartitionKey(context), general));
            options.AddPolicy(SensitivePolicy, context => FixedWindow(PartitionKey(context), sensitive));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Problems.Of(StatusCodes.Status429TooManyRequests, "rate_limited", "Muitas requisições. Aguarde um minuto e tente de novo.")
                    .ExecuteAsync(context.HttpContext);
            };
        });

        return services;
    }

    /// <summary>A API nao serve HTML: esta CSP impede que qualquer resposta execute ou carregue algo.</summary>
    public static IApplicationBuilder UseHubSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
                || context.Request.Path.StartsWithSegments("/auth", StringComparison.Ordinal))
            {
                headers.CacheControl = "no-store";
            }

            await next();
        });

    private static string PartitionKey(HttpContext context)
    {
        var subject = context.User.FindFirstValue("sub");
        return subject is not null
            ? $"actor:{context.User.FindFirstValue("tenant")}:{subject}"
            : $"ip:{context.Connection.RemoteIpAddress}";
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}
