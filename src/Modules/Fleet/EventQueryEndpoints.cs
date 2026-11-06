using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.Fleet;

public sealed record AccessEventResponse(
    Guid Id,
    Guid DeviceId,
    string SourceFamily,
    string? SourceEventId,
    string? SourceEventCode,
    string EventKind,
    DateTime? OccurredAt,
    DateTime? ObservedAt,
    DateTime IngestedAt,
    string IngressSource,
    JsonElement Details);

public sealed record AccessEventPage(IReadOnlyList<AccessEventResponse> Items, string? NextCursor);

public static class EventQueryEndpoints
{
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapEventQueryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/events", ListAsync).WithTags("Events").Produces<AccessEventPage>().RequirePermission(Permissions.EventsRead);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        RequestAccess access,
        ITenantDatabase database,
        Guid? deviceId,
        string? eventKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var size = limit ?? 50;
        if (size is < 1 or > MaxLimit)
        {
            return Problems.Invalid($"O limite deve ficar entre 1 e {MaxLimit}.");
        }

        (DateTime At, Guid Id)? position = null;
        if (cursor is not null)
        {
            if (!TryDecode(cursor, out var decoded))
            {
                return Problems.Invalid("Cursor inválido.");
            }

            position = decoded;
        }

        var scope = http.GetScope();
        var rows = (await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<EventRow>(
                """
                SELECT e.id AS Id, e.device_id AS DeviceId, e.source_family AS SourceFamily, e.source_event_id AS SourceEventId,
                       e.source_event_code AS SourceEventCode, e.event_kind AS EventKind, e.occurred_at AS OccurredAt,
                       e.observed_at AS ObservedAt, e.ingested_at AS IngestedAt, e.ingress_source AS IngressSource, e.details::text AS Details
                FROM hub.access_events e
                JOIN hub.devices d ON d.tenant_id = e.tenant_id AND d.id = e.device_id
                WHERE (@all OR d.site_id = ANY(@allowed)) AND NOT d.site_id = ANY(@denied)
                  AND (@deviceId::uuid IS NULL OR e.device_id = @deviceId)
                  AND (@eventKind::text IS NULL OR e.event_kind = @eventKind)
                  AND (@from::timestamptz IS NULL OR e.occurred_at >= @from)
                  AND (@to::timestamptz IS NULL OR e.occurred_at < @to)
                  AND (@cursorAt::timestamptz IS NULL OR (e.ingested_at, e.id) < (@cursorAt, @cursorId))
                ORDER BY e.ingested_at DESC, e.id DESC
                LIMIT @take
                """,
                new
                {
                    all = scope.AllSites,
                    allowed = scope.AllowedSites.ToArray(),
                    denied = scope.DeniedSites.ToArray(),
                    deviceId,
                    eventKind,
                    from = from?.UtcDateTime,
                    to = to?.UtcDateTime,
                    cursorAt = position?.At,
                    cursorId = position?.Id,
                    take = size + 1,
                }),
            cancellationToken)).ToList();

        var page = rows.Take(size).ToList();
        var next = rows.Count > size ? Encode(page[^1].IngestedAt, page[^1].Id) : null;
        return Results.Ok(new AccessEventPage(page.Select(ToResponse).ToList(), next));
    }

    private static AccessEventResponse ToResponse(EventRow row)
    {
        using var details = JsonDocument.Parse(row.Details);
        return new AccessEventResponse(row.Id, row.DeviceId, row.SourceFamily, row.SourceEventId, row.SourceEventCode, row.EventKind, row.OccurredAt, row.ObservedAt, row.IngestedAt, row.IngressSource, details.RootElement.Clone());
    }

    private static string Encode(DateTime at, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{at.Ticks}|{id:N}")));

    private static bool TryDecode(string cursor, out (DateTime At, Guid Id) position)
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

    private sealed record EventRow(
        Guid Id,
        Guid DeviceId,
        string SourceFamily,
        string? SourceEventId,
        string? SourceEventCode,
        string EventKind,
        DateTime? OccurredAt,
        DateTime? ObservedAt,
        DateTime IngestedAt,
        string IngressSource,
        string Details);
}
