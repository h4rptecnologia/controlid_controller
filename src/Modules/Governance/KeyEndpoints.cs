using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

namespace Platform.Modules.Governance;

public sealed record RewrapResponse(string Purpose, int Rewrapped, long Remaining);

public static class KeyEndpoints
{
    private const int BatchSize = 500;

    public static IEndpointRouteBuilder MapKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/keys").WithTags("Keys");
        group.MapPost("/{purpose}/rotate", RotateAsync).Produces(StatusCodes.Status204NoContent).RequireRateLimiting("sensitive").RequirePermission(Permissions.KeysRotate).RequireIdempotency();
        group.MapPost("/{purpose}/rewrap", RewrapAsync).Produces<RewrapResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.KeysRotate);
        return routes;
    }

    private static async Task<IResult> RotateAsync(string purpose, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().IsTenantWide)
        {
            return Problems.Forbidden();
        }

        if (!TryParse(purpose, out var keyPurpose))
        {
            return Problems.Invalid("Finalidade de chave desconhecida.");
        }

        if (http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return Unavailable();
        }

        try
        {
            await store.RotateKeyAsync(access.Actor.TenantId, keyPurpose, cancellationToken);
        }
        catch (KeyServiceException)
        {
            return Unavailable();
        }

        await AuditAsync(http, access, database, "keys.rotate", purpose, null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RewrapAsync(string purpose, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().IsTenantWide)
        {
            return Problems.Forbidden();
        }

        if (!TryParse(purpose, out var keyPurpose))
        {
            return Problems.Invalid("Finalidade de chave desconhecida.");
        }

        if (http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return Unavailable();
        }

        RewrapResult result;
        try
        {
            result = await store.RewrapBatchAsync(access.Actor.TenantId, keyPurpose, BatchSize, cancellationToken);
        }
        catch (KeyServiceException)
        {
            return Unavailable();
        }

        await AuditAsync(http, access, database, "keys.rewrap", purpose, new { result.Rewrapped, result.Remaining }, cancellationToken);
        return Results.Ok(new RewrapResponse(purpose, result.Rewrapped, result.Remaining));
    }

    private static async Task AuditAsync(HttpContext http, RequestAccess access, ITenantDatabase database, string action, string purpose, object? metadata, CancellationToken cancellationToken) =>
        await database.InTenantAsync(
            access.Actor.TenantId,
            async tx =>
            {
                await AuditWriter.WriteAsync(tx, new AuditEntry(
                    access.Actor.ActorId, action, "key", purpose, AuditWriter.Success, http.GetCorrelationId(), Metadata: metadata));
                return true;
            },
            cancellationToken);

    private static bool TryParse(string value, out KeyPurpose purpose)
    {
        switch (value)
        {
            case "general":
                purpose = KeyPurpose.General;
                return true;
            case "biometric":
                purpose = KeyPurpose.Biometric;
                return true;
            case "secret":
                purpose = KeyPurpose.Secret;
                return true;
            default:
                purpose = default;
                return false;
        }
    }

    private static IResult Unavailable() =>
        Problems.Of(StatusCodes.Status503ServiceUnavailable, "key_service_unavailable", "Serviço de chaves indisponível.");
}
