using Microsoft.AspNetCore.Http;

namespace Platform.Shared.Kernel.Http;

/// <summary>Contrato de erro de docs/12-contratos.md: RFC 9457 com um codigo estavel.</summary>
public static class Problems
{
    public static IResult Of(int status, string code, string title) =>
        Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Invalid(string title) => Of(StatusCodes.Status400BadRequest, "invalid_input", title);

    public static IResult Forbidden(string code = "forbidden", string title = "Você não tem permissão para esta ação.") =>
        Of(StatusCodes.Status403Forbidden, code, title);

    // A mesma resposta para "nao existe" e "fora do seu escopo": nao revela a existencia em outro tenant.
    public static IResult NotFound() =>
        Of(StatusCodes.Status404NotFound, "not_found_or_not_in_scope", "Recurso não encontrado.");

    public static IResult Conflict(string title) => Of(StatusCodes.Status409Conflict, "conflict", title);

    /// <summary>Codigo padrao para respostas sem corpo proprio, como as geradas pelo pipeline.</summary>
    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "invalid_input",
        StatusCodes.Status401Unauthorized => "unauthenticated",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found_or_not_in_scope",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status412PreconditionFailed => "version_precondition_failed",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        _ => "error",
    };
}
