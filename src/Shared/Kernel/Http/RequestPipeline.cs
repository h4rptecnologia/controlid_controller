using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Platform.Shared.Kernel.Http;

public static class RequestPipeline
{
    public const string CorrelationHeader = "X-Correlation-ID";
    public const string CsrfHeader = "X-Hub-Csrf";
    private const string CorrelationItem = "hub.correlation";

    public static Guid GetCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[CorrelationItem] is Guid id ? id : Guid.Empty;
    }

    /// <summary>Aceita so GUID: o valor vai para auditoria e nao pode carregar texto arbitrario.</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var id = Guid.TryParse(context.Request.Headers[CorrelationHeader], out var parsed) && parsed != Guid.Empty
                ? parsed
                : Guid.NewGuid();
            context.Items[CorrelationItem] = id;
            context.Response.Headers[CorrelationHeader] = id.ToString();
            await next();
        });

    /// <summary>
    /// CSRF para a sessao por cookie: escrita exige cabecalho que um formulario de outro site nao consegue
    /// enviar, e o Origin, quando presente, precisa ser o proprio host.
    /// </summary>
    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsProtected(context.Request) && !IsAllowed(context.Request))
            {
                await Problems.Forbidden("csrf_failed", "Requisição recusada pela proteção contra CSRF.").ExecuteAsync(context);
                return;
            }

            await next();
        });

    private static bool IsProtected(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
        && (request.Path.StartsWithSegments("/api", StringComparison.Ordinal) || request.Path.StartsWithSegments("/auth", StringComparison.Ordinal))
        && !request.Path.StartsWithSegments("/auth/callback", StringComparison.Ordinal);

    private static bool IsAllowed(HttpRequest request)
    {
        if (!request.Headers.ContainsKey(CsrfHeader))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
            || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }
}
