using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.Governance;

public sealed record AuditEntryResponse(
    Guid Id,
    Guid? ActorId,
    string? ActorName,
    string Action,
    string ResourceType,
    string? ResourceId,
    string? Reason,
    string Outcome,
    Guid? CorrelationId,
    JsonElement Metadata,
    DateTime CreatedAt);

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, string? NextCursor);

public static class AuditEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/audit", ListAsync)
            .WithTags("Audit")
            .Produces<AuditPageResponse>()
            .RequirePermission(Permissions.TenantAuditRead);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        RequestAccess access,
        ITenantDatabase database,
        string? action,
        string? outcome,
        Guid? actorId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        // A trilha cobre o tenant inteiro: um auditor limitado a uma unidade veria eventos das outras.
        if (!http.GetScope().IsTenantWide)
        {
            return Problems.Forbidden();
        }

        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return Problems.Invalid($"O limite deve ficar entre 1 e {MaxLimit}.");
        }

        (DateTime At, Guid Id)? position = null;
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var decoded))
            {
                return Problems.Invalid("Cursor inválido.");
            }

            position = decoded;
        }

        var rows = (await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<AuditRow>(
                """
                SELECT l.id AS Id, l.actor_id AS ActorId, a.display_name AS ActorName, l.action AS Action, l.resource_type AS ResourceType,
                       l.resource_id AS ResourceId, l.reason AS Reason, l.outcome AS Outcome, l.correlation_id AS CorrelationId,
                       l.metadata::text AS Metadata, l.created_at AS CreatedAt
                FROM hub.audit_log l
                LEFT JOIN hub.actors a ON a.tenant_id = l.tenant_id AND a.id = l.actor_id
                WHERE (@action::text IS NULL OR l.action = @action)
                  AND (@outcome::text IS NULL OR l.outcome = @outcome)
                  AND (@actorId::uuid IS NULL OR l.actor_id = @actorId)
                  AND (@from::timestamptz IS NULL OR l.created_at >= @from)
                  AND (@to::timestamptz IS NULL OR l.created_at < @to)
                  AND (@cursorAt::timestamptz IS NULL OR (l.created_at, l.id) < (@cursorAt, @cursorId))
                ORDER BY l.created_at DESC, l.id DESC
                LIMIT @take
                """,
                new
                {
                    action,
                    outcome,
                    actorId,
                    from = from?.UtcDateTime,
                    to = to?.UtcDateTime,
                    cursorAt = position?.At,
                    cursorId = position?.Id,
                    take = size + 1,
                }),
            cancellationToken)).ToList();

        var page = rows.Take(size).ToList();
        var next = rows.Count > size ? EncodeCursor(page[^1].CreatedAt, page[^1].Id) : null;

        return Results.Ok(new AuditPageResponse(page.Select(ToResponse).ToList(), next));
    }

    private static AuditEntryResponse ToResponse(AuditRow row)
    {
        using var metadata = JsonDocument.Parse(row.Metadata);
        return new AuditEntryResponse(
            row.Id,
            row.ActorId,
            row.ActorName,
            row.Action,
            row.ResourceType,
            row.ResourceId,
            row.Reason,
            row.Outcome,
            row.CorrelationId,
            metadata.RootElement.Clone(),
            row.CreatedAt);
    }

    private static string EncodeCursor(DateTime at, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{at.Ticks}|{id:N}")));

    private static bool TryDecodeCursor(string cursor, out (DateTime At, Guid Id) position)
    {
        position = default;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTime.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id))
            {
                position = (new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        return false;
    }

    private sealed record AuditRow(
        Guid Id,
        Guid? ActorId,
        string? ActorName,
        string Action,
        string ResourceType,
        string? ResourceId,
        string? Reason,
        string Outcome,
        Guid? CorrelationId,
        string Metadata,
        DateTime CreatedAt);
}
