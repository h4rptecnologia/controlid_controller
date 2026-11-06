using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;

namespace Platform.Modules.AccessPolicy;

public sealed record CreateNamedRequest(Guid SiteId, string? Name);

public sealed record NamedResponse(Guid Id, Guid SiteId, string Name, DateTime CreatedAt);

public sealed record CreatePortalRequest(Guid SiteId, string? Name, Guid AreaFromId, Guid AreaToId);

public sealed record PortalResponse(Guid Id, Guid SiteId, string Name, Guid AreaFromId, Guid AreaToId, DateTime CreatedAt);

public sealed record SpanRequest(int StartSecond, int EndSecond, IReadOnlyList<string>? Weekdays, IReadOnlyList<int>? HolidayTypes);

public sealed record CreateScheduleRequest(Guid SiteId, string? Name, string? Timezone, IReadOnlyList<SpanRequest>? Spans);

public sealed record SpanResponse(int StartSecond, int EndSecond, IReadOnlyList<string> Weekdays, IReadOnlyList<int> HolidayTypes);

public sealed record ScheduleResponse(Guid Id, Guid SiteId, string Name, string Timezone, IReadOnlyList<SpanResponse> Spans, DateTime CreatedAt);

public sealed record CreateHolidayRequest(Guid SiteId, string? Name, DateOnly StartsOn, DateOnly EndsOn, int HolidayType, bool Repeats);

public sealed record HolidayResponse(Guid Id, Guid SiteId, string Name, DateOnly StartsOn, DateOnly EndsOn, int HolidayType, bool Repeats);

/// <summary>Versionada e fechada: so identificadores que existem nesta unidade, nunca script ou texto livre.</summary>
public sealed record PolicySpec(IReadOnlyList<Guid>? Groups, IReadOnlyList<Guid>? People, IReadOnlyList<Guid>? Portals, IReadOnlyList<Guid>? Schedules);

public sealed record CreatePolicyRequest(Guid SiteId, string? Kind, string? Name, PolicySpec? Spec);

public sealed record UpdatePolicyRequest(string? Name, PolicySpec? Spec);

public sealed record PolicyResponse(Guid Id, Guid SiteId, string Kind, string Name, PolicySpec Spec, int SpecVersion, int Version, DateTime? ApprovedAt, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record PersonGroupRequest(Guid GroupId);

/// <summary>
/// Modelo canonico de acesso (RF-009 a RF-012). So define; nada e enviado a equipamento aqui. A prevalencia de bloqueio
/// sobre permissao (RF-011) ainda nao e calculada: vem com o simulador do E04.4 e precisa ser confirmada em hardware.
/// </summary>
public static class AccessEndpoints
{
    private const int SpecVersion = 1;
    private const int MaxListItems = 500;
    private const int MaxSpans = 50;
    private static readonly HashSet<string> Weekdays = new(StringComparer.Ordinal) { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string PolicyColumns =
        """
        id AS Id, site_id AS SiteId, kind AS Kind, name AS Name, spec::text AS SpecText, spec_version AS SpecVersion, version AS Version,
        approved_at AS ApprovedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder routes)
    {
        var access = routes.MapGroup("/api/v1/access").WithTags("Access");
        access.MapGet("/groups", (HttpContext http, RequestAccess a, ITenantDatabase d, CancellationToken c) => ListNamedAsync("access_groups", http, a, d, c)).Produces<IEnumerable<NamedResponse>>().RequirePermission(Permissions.PoliciesRead);
        access.MapPost("/groups", (CreateNamedRequest body, HttpContext http, RequestAccess a, ITenantDatabase d, CancellationToken c) => CreateNamedAsync("access_groups", "access.group.create", "group", body, http, a, d, c)).Produces<NamedResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();
        access.MapGet("/areas", (HttpContext http, RequestAccess a, ITenantDatabase d, CancellationToken c) => ListNamedAsync("areas", http, a, d, c)).Produces<IEnumerable<NamedResponse>>().RequirePermission(Permissions.PoliciesRead);
        access.MapPost("/areas", (CreateNamedRequest body, HttpContext http, RequestAccess a, ITenantDatabase d, CancellationToken c) => CreateNamedAsync("areas", "access.area.create", "area", body, http, a, d, c)).Produces<NamedResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();
        access.MapGet("/portals", ListPortalsAsync).Produces<IEnumerable<PortalResponse>>().RequirePermission(Permissions.PoliciesRead);
        access.MapPost("/portals", CreatePortalAsync).Produces<PortalResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();
        access.MapGet("/schedules", ListSchedulesAsync).Produces<IEnumerable<ScheduleResponse>>().RequirePermission(Permissions.PoliciesRead);
        access.MapPost("/schedules", CreateScheduleAsync).Produces<ScheduleResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();
        access.MapGet("/holidays", ListHolidaysAsync).Produces<IEnumerable<HolidayResponse>>().RequirePermission(Permissions.PoliciesRead);
        access.MapPost("/holidays", CreateHolidayAsync).Produces<HolidayResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();

        var policies = routes.MapGroup("/api/v1/policies").WithTags("Access");
        policies.MapGet("/", ListPoliciesAsync).Produces<IEnumerable<PolicyResponse>>().RequirePermission(Permissions.PoliciesRead);
        policies.MapPost("/", CreatePolicyAsync).Produces<PolicyResponse>(StatusCodes.Status201Created).RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();
        policies.MapPatch("/{id:guid}", UpdatePolicyAsync).Produces<PolicyResponse>().RequirePermission(Permissions.PoliciesWrite).RequireIdempotency();

        var people = routes.MapGroup("/api/v1/people").WithTags("People");
        people.MapGet("/{id:guid}/groups", GroupsOfPersonAsync).Produces<IEnumerable<NamedResponse>>().RequirePermission(Permissions.PeopleRead);
        people.MapPost("/{id:guid}/groups", AddToGroupAsync).Produces(StatusCodes.Status204NoContent).RequirePermission(Permissions.PeopleWrite).RequireIdempotency();
        people.MapDelete("/{id:guid}/groups/{groupId:guid}", RemoveFromGroupAsync).Produces(StatusCodes.Status204NoContent).RequirePermission(Permissions.PeopleWrite).RequireIdempotency();
        return routes;
    }

    private static object ScopeArgs(PermissionScope scope) =>
        new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() };

    private const string ScopeSql = "(@all OR site_id = ANY(@allowed)) AND NOT site_id = ANY(@denied)";

    private static string? ValidName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) ? "Informe o nome, com até 200 caracteres." : null;

    private static async Task<IResult> ListNamedAsync(string table, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<NamedResponse>($"SELECT id AS Id, site_id AS SiteId, name AS Name, created_at AS CreatedAt FROM hub.{table} WHERE {ScopeSql} ORDER BY lower(name), id LIMIT {MaxListItems}", ScopeArgs(scope)),
            cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateNamedAsync(string table, string action, string resource, CreateNamedRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if (ValidName(body.Name) is { } problem)
        {
            return Problems.Invalid(problem);
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                    {
                        return Problems.NotFound();
                    }

                    var created = await tx.QuerySingleOrDefaultAsync<NamedResponse>(
                        $"INSERT INTO hub.{table} (tenant_id, site_id, name) VALUES (@TenantId, @SiteId, @name) RETURNING id AS Id, site_id AS SiteId, name AS Name, created_at AS CreatedAt",
                        new { tx.TenantId, body.SiteId, name = body.Name!.Trim() });
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, action, resource, created!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    return Results.Created($"/api/v1/access/{table}/{created.Id}", created);
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return NameInUse();
        }
    }

    private static async Task<IResult> ListPortalsAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        return Results.Ok(await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<PortalResponse>($"SELECT id AS Id, site_id AS SiteId, name AS Name, area_from_id AS AreaFromId, area_to_id AS AreaToId, created_at AS CreatedAt FROM hub.portals WHERE {ScopeSql} ORDER BY lower(name), id LIMIT {MaxListItems}", ScopeArgs(scope)),
            cancellationToken));
    }

    private static async Task<IResult> CreatePortalAsync(CreatePortalRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if (ValidName(body.Name) is { } problem)
        {
            return Problems.Invalid(problem);
        }

        if (body.AreaFromId == body.AreaToId)
        {
            return Problems.Invalid("O portal precisa ligar duas áreas diferentes.");
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    var created = await tx.QuerySingleOrDefaultAsync<PortalResponse>(
                        """
                        INSERT INTO hub.portals (tenant_id, site_id, name, area_from_id, area_to_id) VALUES (@TenantId, @SiteId, @name, @AreaFromId, @AreaToId)
                        RETURNING id AS Id, site_id AS SiteId, name AS Name, area_from_id AS AreaFromId, area_to_id AS AreaToId, created_at AS CreatedAt
                        """,
                        new { tx.TenantId, body.SiteId, name = body.Name!.Trim(), body.AreaFromId, body.AreaToId });
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "access.portal.create", "portal", created!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    return Results.Created($"/api/v1/access/portals/{created.Id}", created);
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return NameInUse();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Problems.Invalid("As duas áreas precisam existir nesta unidade.");
        }
    }

    private static async Task<IResult> ListSchedulesAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            async tx =>
            {
                var schedules = (await tx.QueryAsync<ScheduleRow>($"SELECT id AS Id, site_id AS SiteId, name AS Name, timezone AS Timezone, created_at AS CreatedAt FROM hub.schedules WHERE {ScopeSql} ORDER BY lower(name), id LIMIT {MaxListItems}", ScopeArgs(scope))).ToList();
                var spans = (await tx.QueryAsync<SpanRow>(
                    "SELECT schedule_id AS ScheduleId, start_second AS StartSecond, end_second AS EndSecond, array_to_string(weekdays, ',') AS WeekdaysText, array_to_string(holiday_types, ',') AS HolidaysText FROM hub.schedule_spans WHERE schedule_id = ANY(@ids) ORDER BY start_second",
                    new { ids = schedules.Select(s => s.Id).ToArray() })).ToLookup(s => s.ScheduleId);
                return schedules.Select(s => new ScheduleResponse(s.Id, s.SiteId, s.Name, s.Timezone, spans[s.Id].Select(ToSpan).ToList(), s.CreatedAt)).ToList();
            },
            cancellationToken);
        return Results.Ok(rows);
    }

    /// <summary>Nulo quando valido. Horario sempre com fuso explicito, e virar a noite sao dois intervalos.</summary>
    internal static string? ValidateSchedule(string? timezone, IReadOnlyList<SpanRequest>? spans)
    {
        if (string.IsNullOrEmpty(timezone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _) || !timezone.Contains('/', StringComparison.Ordinal))
        {
            return "Informe um fuso horário válido, como America/Sao_Paulo.";
        }

        if (spans is not { Count: > 0 and <= MaxSpans })
        {
            return $"Informe de 1 a {MaxSpans} intervalos.";
        }

        foreach (var span in spans)
        {
            if (span.StartSecond is < 0 or > 86399 || span.EndSecond is < 1 or > 86400)
            {
                return "Horário fora do dia.";
            }

            if (span.StartSecond >= span.EndSecond)
            {
                return "O início precisa ser antes do fim. Para virar a noite, cadastre dois intervalos: até 24:00 e a partir de 00:00.";
            }

            var days = span.Weekdays ?? [];
            var holidays = span.HolidayTypes ?? [];
            if (days.Any(d => !Weekdays.Contains(d)) || holidays.Any(h => h is < 1 or > 3))
            {
                return "Dia da semana ou tipo de feriado inválido.";
            }

            if (days.Count == 0 && holidays.Count == 0)
            {
                return "Cada intervalo precisa de ao menos um dia da semana ou tipo de feriado.";
            }
        }

        return null;
    }

    private static async Task<IResult> CreateScheduleAsync(CreateScheduleRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if ((ValidName(body.Name) ?? ValidateSchedule(body.Timezone, body.Spans)) is { } problem)
        {
            return Problems.Invalid(problem);
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                    {
                        return Problems.NotFound();
                    }

                    var schedule = await tx.QuerySingleOrDefaultAsync<ScheduleRow>(
                        "INSERT INTO hub.schedules (tenant_id, site_id, name, timezone) VALUES (@TenantId, @SiteId, @name, @Timezone) RETURNING id AS Id, site_id AS SiteId, name AS Name, timezone AS Timezone, created_at AS CreatedAt",
                        new { tx.TenantId, body.SiteId, name = body.Name!.Trim(), body.Timezone });
                    foreach (var span in body.Spans!)
                    {
                        await tx.ExecuteAsync(
                            "INSERT INTO hub.schedule_spans (tenant_id, schedule_id, start_second, end_second, weekdays, holiday_types) VALUES (@TenantId, @id, @StartSecond, @EndSecond, @days, @holidays)",
                            new { tx.TenantId, id = schedule!.Id, span.StartSecond, span.EndSecond, days = (span.Weekdays ?? []).Distinct().ToArray(), holidays = (span.HolidayTypes ?? []).Distinct().ToArray() });
                    }

                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "access.schedule.create", "schedule", schedule!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    var spans = body.Spans.Select(s => new SpanResponse(s.StartSecond, s.EndSecond, s.Weekdays ?? [], s.HolidayTypes ?? [])).ToList();
                    return Results.Created($"/api/v1/access/schedules/{schedule.Id}", new ScheduleResponse(schedule.Id, schedule.SiteId, schedule.Name, schedule.Timezone, spans, schedule.CreatedAt));
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return NameInUse();
        }
    }

    private static async Task<IResult> ListHolidaysAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<HolidayRow>($"SELECT id AS Id, site_id AS SiteId, name AS Name, starts_on::timestamp AS StartsOn, ends_on::timestamp AS EndsOn, holiday_type AS HolidayType, repeats AS Repeats FROM hub.holidays WHERE {ScopeSql} ORDER BY starts_on, id LIMIT {MaxListItems}", ScopeArgs(scope)),
            cancellationToken);
        return Results.Ok(rows.Select(ToHoliday));
    }

    private static async Task<IResult> CreateHolidayAsync(CreateHolidayRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if (ValidName(body.Name) is { } problem)
        {
            return Problems.Invalid(problem);
        }

        if (body.EndsOn < body.StartsOn || body.HolidayType is < 1 or > 3)
        {
            return Problems.Invalid("Confira as datas e o tipo de feriado, de 1 a 3.");
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                {
                    return Problems.NotFound();
                }

                var created = await tx.QuerySingleOrDefaultAsync<HolidayRow>(
                    """
                    INSERT INTO hub.holidays (tenant_id, site_id, name, starts_on, ends_on, holiday_type, repeats) VALUES (@TenantId, @SiteId, @name, @from::date, @to::date, @HolidayType, @Repeats)
                    RETURNING id AS Id, site_id AS SiteId, name AS Name, starts_on::timestamp AS StartsOn, ends_on::timestamp AS EndsOn, holiday_type AS HolidayType, repeats AS Repeats
                    """,
                    new { tx.TenantId, body.SiteId, name = body.Name!.Trim(), from = body.StartsOn.ToDateTime(TimeOnly.MinValue), to = body.EndsOn.ToDateTime(TimeOnly.MinValue), body.HolidayType, body.Repeats });
                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "access.holiday.create", "holiday", created!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.Created($"/api/v1/access/holidays/{created.Id}", ToHoliday(created));
            },
            cancellationToken);
    }

    private static async Task<IResult> ListPoliciesAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<PolicyRow>($"SELECT {PolicyColumns} FROM hub.access_policies WHERE {ScopeSql} ORDER BY lower(name), id LIMIT {MaxListItems}", ScopeArgs(scope)),
            cancellationToken);
        return Results.Ok(rows.Select(ToPolicy));
    }

    internal static string? ValidateSpecShape(PolicySpec? spec)
    {
        if (spec is null)
        {
            return "Informe a quem e onde a regra se aplica.";
        }

        if ((spec.Groups?.Count ?? 0) + (spec.People?.Count ?? 0) == 0)
        {
            return "A regra precisa de ao menos um grupo ou pessoa.";
        }

        if ((spec.Portals?.Count ?? 0) == 0)
        {
            return "A regra precisa de ao menos um portal.";
        }

        return new[] { spec.Groups, spec.People, spec.Portals, spec.Schedules }.Any(list => (list?.Count ?? 0) > MaxListItems) ? "A regra tem itens demais." : null;
    }

    /// <summary>Toda referencia precisa existir nesta unidade: politica nao aponta para o que nao existe nem atravessa unidade.</summary>
    private static async Task<bool> ReferencesExistAsync(TenantTransaction tx, Guid site, PolicySpec spec)
    {
        async Task<bool> AllIn(string sql, IReadOnlyList<Guid>? ids)
        {
            var distinct = (ids ?? []).Distinct().ToArray();
            return distinct.Length == 0 || await tx.QuerySingleOrDefaultAsync<int>(sql, new { ids = distinct, site }) == distinct.Length;
        }

        return await AllIn("SELECT count(*)::int FROM hub.access_groups WHERE id = ANY(@ids) AND site_id = @site", spec.Groups)
            && await AllIn("SELECT count(*)::int FROM hub.portals WHERE id = ANY(@ids) AND site_id = @site", spec.Portals)
            && await AllIn("SELECT count(*)::int FROM hub.schedules WHERE id = ANY(@ids) AND site_id = @site", spec.Schedules)
            && await AllIn("SELECT count(*)::int FROM hub.people WHERE id = ANY(@ids) AND (site_id = @site OR site_id IS NULL) AND deleted_at IS NULL", spec.People);
    }

    private static async Task<IResult> CreatePolicyAsync(CreatePolicyRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!http.GetScope().Includes(body.SiteId))
        {
            return Problems.NotFound();
        }

        if (body.Kind is not ("allow" or "deny"))
        {
            return Problems.Invalid("A regra precisa ser de permissão ou de bloqueio.");
        }

        if ((ValidName(body.Name) ?? ValidateSpecShape(body.Spec)) is { } problem)
        {
            return Problems.Invalid(problem);
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    if (await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.sites WHERE id = @SiteId", new { body.SiteId }) is null)
                    {
                        return Problems.NotFound();
                    }

                    if (!await ReferencesExistAsync(tx, body.SiteId, body.Spec!))
                    {
                        return Problems.Of(StatusCodes.Status422UnprocessableEntity, "reference_not_found", "A regra cita algo que não existe nesta unidade.");
                    }

                    var created = await tx.QuerySingleOrDefaultAsync<PolicyRow>(
                        $"""
                        INSERT INTO hub.access_policies (tenant_id, site_id, kind, name, spec, spec_version, created_by)
                        VALUES (@TenantId, @SiteId, @Kind, @name, @spec::jsonb, @version, @actor)
                        RETURNING {PolicyColumns}
                        """,
                        new { tx.TenantId, body.SiteId, body.Kind, name = body.Name!.Trim(), spec = JsonSerializer.Serialize(body.Spec, Json), version = SpecVersion, actor = access.Actor.ActorId });
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "policy.create", "policy", created!.Id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { kind = body.Kind }));
                    return Results.Created($"/api/v1/policies/{created.Id}", ToPolicy(created));
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return NameInUse();
        }
    }

    private static async Task<IResult> UpdatePolicyAsync(Guid id, UpdatePolicyRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (!int.TryParse(http.Request.Headers.IfMatch.ToString().Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
        {
            return Problems.Of(StatusCodes.Status428PreconditionRequired, "version_required", "Informe a versão que você está editando no cabeçalho If-Match.");
        }

        if ((body.Name is not null ? ValidName(body.Name) : null) is { } nameProblem)
        {
            return Problems.Invalid(nameProblem);
        }

        if ((body.Spec is not null ? ValidateSpecShape(body.Spec) : null) is { } specProblem)
        {
            return Problems.Invalid(specProblem);
        }

        try
        {
            return await database.InTenantAsync<IResult>(
                access.Actor.TenantId,
                async tx =>
                {
                    var current = await tx.QuerySingleOrDefaultAsync<PolicyRow>($"SELECT {PolicyColumns} FROM hub.access_policies WHERE id = @id", new { id });
                    if (current is null || !http.GetScope().Includes(current.SiteId))
                    {
                        return Problems.NotFound();
                    }

                    if (body.Spec is not null && !await ReferencesExistAsync(tx, current.SiteId, body.Spec))
                    {
                        return Problems.Of(StatusCodes.Status422UnprocessableEntity, "reference_not_found", "A regra cita algo que não existe nesta unidade.");
                    }

                    // Mudou a regra, a aprovacao anterior deixa de valer.
                    var updated = await tx.QuerySingleOrDefaultAsync<PolicyRow>(
                        $"""
                        UPDATE hub.access_policies
                        SET name = COALESCE(@name, name), spec = COALESCE(@spec::jsonb, spec), version = version + 1, updated_at = now(), approved_at = NULL
                        WHERE id = @id AND version = @expected
                        RETURNING {PolicyColumns}
                        """,
                        new { name = body.Name?.Trim(), spec = body.Spec is null ? null : JsonSerializer.Serialize(body.Spec, Json), id, expected });

                    if (updated is null)
                    {
                        return Problems.Of(StatusCodes.Status412PreconditionFailed, "version_conflict", "Outra pessoa alterou esta regra. Abra de novo para ver a versão atual.");
                    }

                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "policy.update", "policy", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    return Results.Ok(ToPolicy(updated));
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return NameInUse();
        }
    }

    private static async Task<PersonGate?> PersonAsync(TenantTransaction tx, Guid id) =>
        await tx.QuerySingleOrDefaultAsync<PersonGate>("SELECT site_id AS SiteId FROM hub.people WHERE id = @id AND deleted_at IS NULL", new { id });

    private static bool PersonInScope(HttpContext http, PersonGate? person) =>
        person is not null && (person.SiteId is { } site ? http.GetScope().Includes(site) : http.GetScope().AllSites);

    private static async Task<IResult> GroupsOfPersonAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (!PersonInScope(http, await PersonAsync(tx, id)))
                {
                    return Problems.NotFound();
                }

                return Results.Ok(await tx.QueryAsync<NamedResponse>(
                    """
                    SELECT g.id AS Id, g.site_id AS SiteId, g.name AS Name, g.created_at AS CreatedAt
                    FROM hub.person_groups pg JOIN hub.access_groups g ON g.tenant_id = pg.tenant_id AND g.id = pg.group_id
                    WHERE pg.person_id = @id ORDER BY lower(g.name), g.id
                    """,
                    new { id }));
            },
            cancellationToken);

    private static async Task<IResult> AddToGroupAsync(Guid id, PersonGroupRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var person = await PersonAsync(tx, id);
                if (!PersonInScope(http, person))
                {
                    return Problems.NotFound();
                }

                var groupSite = await tx.QuerySingleOrDefaultAsync<Guid?>("SELECT site_id FROM hub.access_groups WHERE id = @GroupId", new { body.GroupId });
                if (groupSite is not { } site || !http.GetScope().Includes(site))
                {
                    return Problems.NotFound();
                }

                if (person!.SiteId is { } personSite && personSite != site)
                {
                    return Problems.Invalid("O grupo precisa ser da mesma unidade da pessoa.");
                }

                await tx.ExecuteAsync("INSERT INTO hub.person_groups (tenant_id, person_id, group_id) VALUES (@TenantId, @id, @GroupId) ON CONFLICT DO NOTHING", new { tx.TenantId, id, body.GroupId });
                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "person.group_add", "person", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { group = body.GroupId }));
                await OutboxWriter.EnqueueAsync(tx, "person.updated.v1", id, http.GetCorrelationId());
                return Results.NoContent();
            },
            cancellationToken);

    private static async Task<IResult> RemoveFromGroupAsync(Guid id, Guid groupId, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                if (!PersonInScope(http, await PersonAsync(tx, id)))
                {
                    return Problems.NotFound();
                }

                var removed = await tx.ExecuteAsync("DELETE FROM hub.person_groups WHERE person_id = @id AND group_id = @groupId", new { id, groupId });
                if (removed == 0)
                {
                    return Problems.NotFound();
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "person.group_remove", "person", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { group = groupId }));
                await OutboxWriter.EnqueueAsync(tx, "person.updated.v1", id, http.GetCorrelationId());
                return Results.NoContent();
            },
            cancellationToken);

    private static SpanResponse ToSpan(SpanRow row) => new(
        row.StartSecond,
        row.EndSecond,
        row.WeekdaysText.Split(',', StringSplitOptions.RemoveEmptyEntries),
        row.HolidaysText.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList());

    private static HolidayResponse ToHoliday(HolidayRow row) =>
        new(row.Id, row.SiteId, row.Name, DateOnly.FromDateTime(row.StartsOn), DateOnly.FromDateTime(row.EndsOn), row.HolidayType, row.Repeats);

    private static PolicyResponse ToPolicy(PolicyRow row) => new(
        row.Id,
        row.SiteId,
        row.Kind,
        row.Name,
        JsonSerializer.Deserialize<PolicySpec>(row.SpecText, Json)!,
        row.SpecVersion,
        row.Version,
        row.ApprovedAt,
        row.CreatedAt,
        row.UpdatedAt);

    private static IResult NameInUse() =>
        Problems.Of(StatusCodes.Status409Conflict, "name_in_use", "Já existe um item com este nome nesta unidade.");

    private sealed record PersonGate(Guid? SiteId);

    private sealed record ScheduleRow(Guid Id, Guid SiteId, string Name, string Timezone, DateTime CreatedAt);

    private sealed record SpanRow(Guid ScheduleId, int StartSecond, int EndSecond, string WeekdaysText, string HolidaysText);

    private sealed record HolidayRow(Guid Id, Guid SiteId, string Name, DateTime StartsOn, DateTime EndsOn, int HolidayType, bool Repeats);

    private sealed record PolicyRow(Guid Id, Guid SiteId, string Kind, string Name, string SpecText, int SpecVersion, int Version, DateTime? ApprovedAt, DateTime CreatedAt, DateTime UpdatedAt);
}
