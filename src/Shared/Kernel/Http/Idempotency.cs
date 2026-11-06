using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;

namespace Platform.Shared.Kernel.Http;

public sealed record IdempotentMetadata;

public static class Idempotency
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";
    private const int MaxKeyLength = 128;
    private const string InProgress = "in_progress";
    private const string Completed = "completed";
    private const string Failed = "failed";
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>O corpo ja foi lido na vinculacao dos parametros; sem buffer nao daria para calcular o hash.</summary>
    public static IApplicationBuilder UseIdempotencyBuffering(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentMetadata>() is not null)
            {
                context.Request.EnableBuffering();
            }

            await next();
        });

    /// <summary>
    /// Deve vir depois de RequirePermission. A mesma chave com a mesma requisicao devolve a resposta original
    /// sem executar de novo; com outra requisicao, 409.
    /// </summary>
    /// <param name="retryAfterFailure">
    /// Falso para comandos nao idempotentes, como acao fisica: uma falha ou erro fica registrado e a mesma chave
    /// nunca executa outra vez, pois nao se sabe se o efeito ocorreu.
    /// </param>
    public static RouteHandlerBuilder RequireIdempotency(this RouteHandlerBuilder builder, bool retryAfterFailure = true) =>
        builder.WithMetadata(new IdempotentMetadata()).AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var key = http.Request.Headers[KeyHeader].ToString();
            if (!IsValidKey(key))
            {
                return Problems.Of(StatusCodes.Status400BadRequest, "idempotency_key_required", $"Envie o cabeçalho {KeyHeader} com até {MaxKeyLength} caracteres.");
            }

            var actor = http.RequestServices.GetRequiredService<RequestAccess>().Actor;
            var database = http.RequestServices.GetRequiredService<ITenantDatabase>();
            var hash = await HashRequestAsync(http.Request, http.RequestAborted);

            var existing = await ReserveAsync(database, actor, key, hash, http.RequestAborted);
            if (existing is not null)
            {
                return await ReplayAsync(http, existing, hash);
            }

            var original = http.Response.Body;
            await using var buffer = new MemoryStream();
            http.Response.Body = buffer;
            try
            {
                var result = await next(context);
                if (result is IResult executable)
                {
                    await executable.ExecuteAsync(http);
                }

                var status = http.Response.StatusCode;
                if (status < StatusCodes.Status400BadRequest || !retryAfterFailure)
                {
                    await StoreAsync(database, actor, key, status < StatusCodes.Status500InternalServerError ? Completed : Failed, http.Response, buffer.ToArray());
                }
                else
                {
                    await ReleaseAsync(database, actor, key);
                }
            }
            catch
            {
                if (retryAfterFailure)
                {
                    await ReleaseAsync(database, actor, key);
                }
                else
                {
                    await StoreAsync(database, actor, key, Failed, null, null);
                }

                throw;
            }
            finally
            {
                http.Response.Body = original;
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, http.RequestAborted);
            return Results.Empty;
        });

    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => c is > ' ' and < (char)127);

    private static async Task<byte[]> HashRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                hash.AppendData(chunk, 0, read);
            }

            request.Body.Position = 0;
        }

        return hash.GetHashAndReset();
    }

    private static Task<StoredRequest?> ReserveAsync(ITenantDatabase database, ActorContext actor, string key, byte[] hash, CancellationToken cancellationToken) =>
        database.InTenantAsync(
            actor.TenantId,
            async tx =>
            {
                // Atomico: em pedidos simultaneos so um consegue a reserva. Chave vencida pode ser reutilizada.
                var reserved = await tx.QuerySingleOrDefaultAsync<int?>(
                    """
                    INSERT INTO hub.idempotency_keys AS k (tenant_id, actor_id, idempotency_key, request_hash, expires_at)
                    VALUES (@TenantId, @ActorId, @key, @hash, @expires)
                    ON CONFLICT (tenant_id, actor_id, idempotency_key) DO UPDATE
                      SET request_hash = excluded.request_hash, state = 'in_progress', response_status = NULL,
                          response_content_type = NULL, response_location = NULL, response_body = NULL,
                          created_at = now(), expires_at = excluded.expires_at
                      WHERE k.expires_at < now()
                    RETURNING 1
                    """,
                    new { tx.TenantId, actor.ActorId, key, hash, expires = DateTime.UtcNow.Add(Retention) });

                return reserved is not null
                    ? null
                    : await tx.QuerySingleOrDefaultAsync<StoredRequest>(
                        """
                        SELECT request_hash AS RequestHash, state AS State, response_status AS ResponseStatus,
                               response_content_type AS ResponseContentType, response_location AS ResponseLocation, response_body AS ResponseBody
                        FROM hub.idempotency_keys WHERE actor_id = @ActorId AND idempotency_key = @key
                        """,
                        new { actor.ActorId, key });
            },
            cancellationToken);

    private static async Task<IResult> ReplayAsync(HttpContext http, StoredRequest stored, byte[] hash)
    {
        if (!CryptographicOperations.FixedTimeEquals(stored.RequestHash, hash))
        {
            return Problems.Of(StatusCodes.Status409Conflict, "idempotency_key_reused", "Esta chave já foi usada com outra requisição.");
        }

        if (stored.State == InProgress)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "request_in_progress", "A requisição original ainda está em andamento ou terminou sem resposta. Consulte o estado antes de tentar de novo.");
        }

        if (stored.ResponseStatus is not { } status)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "previous_attempt_failed", "A tentativa anterior falhou e não será repetida automaticamente.");
        }

        http.Response.StatusCode = status;
        http.Response.Headers[ReplayedHeader] = "true";
        if (stored.ResponseContentType is not null)
        {
            http.Response.ContentType = stored.ResponseContentType;
        }

        if (stored.ResponseLocation is not null)
        {
            http.Response.Headers.Location = stored.ResponseLocation;
        }

        if (stored.ResponseBody is not null)
        {
            await http.Response.Body.WriteAsync(stored.ResponseBody, http.RequestAborted);
        }

        return Results.Empty;
    }

    // CancellationToken.None: o estado precisa ser gravado mesmo se o cliente desconectou.
    private static async Task StoreAsync(ITenantDatabase database, ActorContext actor, string key, string state, HttpResponse? response, byte[]? body) =>
        await database.InTenantAsync(
            actor.TenantId,
            tx => tx.ExecuteAsync(
                """
                UPDATE hub.idempotency_keys
                SET state = @state, response_status = @status, response_content_type = @contentType, response_location = @location, response_body = @body
                WHERE actor_id = @ActorId AND idempotency_key = @key
                """,
                new
                {
                    state,
                    status = response?.StatusCode,
                    contentType = response?.ContentType,
                    location = response?.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
                    body,
                    actor.ActorId,
                    key,
                }),
            CancellationToken.None);

    private static async Task ReleaseAsync(ITenantDatabase database, ActorContext actor, string key) =>
        await database.InTenantAsync(
            actor.TenantId,
            tx => tx.ExecuteAsync("DELETE FROM hub.idempotency_keys WHERE actor_id = @ActorId AND idempotency_key = @key AND state = 'in_progress'", new { actor.ActorId, key }),
            CancellationToken.None);

    private sealed record StoredRequest(byte[] RequestHash, string State, int? ResponseStatus, string? ResponseContentType, string? ResponseLocation, byte[]? ResponseBody);
}
