using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

namespace Platform.Modules.Importation;

/// <param name="SourceSystem">Sistema de origem do arquivo. Com ele, o ID da fonte concilia pela chave externa.</param>
public sealed record CreateImportRunRequest(Guid? DeviceId, TabularMapping? Mapping, Dictionary<string, string>? FieldPolicy, string? SourceSystem = null);

/// <param name="Lines">Linhas escolhidas. Sem elas, vale para todas as linhas de <paramref name="Kind"/>.</param>
public sealed record ImportDecisionRequest(IReadOnlyList<long>? Lines, string? Kind, string? Decision, Guid? PersonId);

public sealed record ApproveImportRequest(string? PreviewDigest, bool AcknowledgePendingReferences);

public sealed record CloseImportRunRequest(string? Reason);

/// <summary>O que a aprovacao autoriza. Nao ha acao fisica nem envio a equipamento neste fluxo.</summary>
public sealed record ImportPlan(long Add, long Update, long Unchanged, long Skipped, long Invalid, long PendingReferences, long Biometrics, long NewCredentials, long CredentialsNotCompared, long NewEvents, long EventsNotCompared);

public sealed record ImportRunResponse(
    Guid Id,
    Guid FileId,
    Guid? SiteId,
    string? OriginalName,
    string? SourceFormat,
    string Status,
    bool DryRun,
    string? ErrorCode,
    Guid? DeviceId,
    string? ParserId,
    PreviewStats? Stats,
    ImportPlan? Plan,
    string? PreviewDigest,
    long UnresolvedDecisions,
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    Guid? SubmittedBy,
    DateTime? SubmittedAt,
    Guid? ApprovedBy,
    DateTime? ApprovedAt,
    Guid? ClosedBy,
    DateTime? ClosedAt,
    string? CloseReason);

public sealed record ImportRowResponse(long LineNo, string SourceObject, string? SourceKey, string Kind, Guid? MatchPersonId, IReadOnlyList<string> Codes, PreviewSample? Sample, string? Decision, Guid? DecisionPersonId);

public sealed record ImportRowPageResponse(IReadOnlyList<ImportRowResponse> Items, long? NextCursor);

public sealed record ImportColumnResponse(int Index, string? Name, IReadOnlyList<string> Samples);

public sealed record ImportColumnsResponse(string Delimiter, IReadOnlyList<ImportColumnResponse> Columns);

public sealed class ImportApprovalOptions
{
    /// <summary>A partir de quantas alteracoes quem preparou nao pode aprovar sozinho.</summary>
    public int DualApprovalThreshold { get; init; } = 100;
}

/// <summary>
/// Previa, decisoes e aprovacao. Quem prepara usa import.create; quem aprova usa import.approve, com MFA e rotas proprias.
/// Aprovar nao importa nada: registra a autorizacao, presa ao resumo que a pessoa viu. A aplicacao chega no E03.5.
/// </summary>
public static class ImportRunEndpoints
{
    private const string RunColumns =
        """
        r.id AS Id, r.file_id AS FileId, f.site_id AS SiteId, f.original_name AS OriginalName, f.source_format AS SourceFormat, r.status AS Status, r.dry_run AS DryRun,
        r.error_code AS ErrorCode, r.device_id AS DeviceId, r.parser_id AS ParserId, r.stats::text AS StatsText, r.plan::text AS PlanText, r.preview_digest AS PreviewDigest,
        (SELECT count(*) FROM hub.import_rows x
          LEFT JOIN hub.import_decisions d ON d.tenant_id = x.tenant_id AND d.run_id = x.run_id AND d.line_no = x.line_no AND d.source_object = x.source_object
          WHERE x.run_id = r.id AND x.result_kind IN ('possible_duplicate', 'field_conflict') AND d.decision IS NULL) AS UnresolvedDecisions,
        r.created_by AS CreatedBy, r.created_at AS CreatedAt, r.expires_at AS ExpiresAt, r.submitted_by AS SubmittedBy, r.submitted_at AS SubmittedAt,
        r.approved_by AS ApprovedBy, r.approved_at AS ApprovedAt, r.closed_by AS ClosedBy, r.closed_at AS ClosedAt, r.close_reason AS CloseReason
        """;

    private const string FromRun = "hub.import_runs r JOIN hub.import_files f ON f.tenant_id = r.tenant_id AND f.id = r.file_id";
    private const int MaxDecisionLines = 5000;
    private const int MaxReason = 500;
    private const int SampleRows = 3;
    private const string ScopeFilter = "(@all OR f.site_id = ANY(@allowed)) AND NOT f.site_id = ANY(@denied)";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapImportRunEndpoints(this IEndpointRouteBuilder routes)
    {
        var files = routes.MapGroup("/api/v1/imports").WithTags("Imports");
        files.MapGet("/{id:guid}/columns", ColumnsAsync).Produces<ImportColumnsResponse>().RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate);
        files.MapPost("/{id:guid}/runs", CreateRunAsync).Produces<ImportRunResponse>(StatusCodes.Status201Created).RequireRateLimiting("sensitive").RequirePermission(Permissions.ImportCreate).RequireIdempotency();
        files.MapGet("/{id:guid}/runs", RunsOfFileAsync).Produces<IEnumerable<ImportRunResponse>>().RequirePermission(Permissions.ImportCreate);

        var runs = routes.MapGroup("/api/v1/import-runs").WithTags("ImportRuns");
        runs.MapGet("/", HistoryAsync).Produces<IEnumerable<ImportRunResponse>>().RequirePermission(Permissions.ImportCreate);
        runs.MapGet("/{id:guid}", GetRunAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportCreate);
        runs.MapGet("/{id:guid}/rows", RowsAsync).Produces<ImportRowPageResponse>().RequirePermission(Permissions.ImportCreate);
        runs.MapGet("/{id:guid}/errors.csv", ErrorsAsync).Produces<string>(contentType: "text/csv").RequirePermission(Permissions.ImportCreate);
        runs.MapPost("/{id:guid}/decisions", DecideAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportCreate).RequireIdempotency();
        runs.MapPost("/{id:guid}/submit", SubmitAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportCreate).RequireIdempotency();
        runs.MapPost("/{id:guid}/cancel", CancelAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportCreate).RequireIdempotency();

        var approvals = routes.MapGroup("/api/v1/import-approvals").WithTags("ImportApprovals");
        approvals.MapGet("/", PendingApprovalsAsync).Produces<IEnumerable<ImportRunResponse>>().RequirePermission(Permissions.ImportApprove);
        approvals.MapGet("/{id:guid}", GetRunAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportApprove);
        approvals.MapGet("/{id:guid}/rows", RowsAsync).Produces<ImportRowPageResponse>().RequirePermission(Permissions.ImportApprove);
        approvals.MapPost("/{id:guid}/approve", ApproveAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportApprove).RequireIdempotency();
        approvals.MapPost("/{id:guid}/reject", RejectAsync).Produces<ImportRunResponse>().RequirePermission(Permissions.ImportApprove).RequireIdempotency();
        return routes;
    }

    private static async Task<IResult> CreateRunAsync(Guid id, CreateImportRunRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.FieldPolicy is { } requested && requested.Any(p => !PersonFields.All.Contains(p.Key) || !FieldPolicies.IsKnown(p.Value)))
        {
            return Problems.Invalid("Política de conflito inválida.");
        }

        if (body.SourceSystem is { Length: 0 or > 64 })
        {
            return Problems.Invalid("O sistema de origem deve ter até 64 caracteres.");
        }

        if (body.Mapping is not null && TabularMappingRules.Validate(body.Mapping) is { } mappingProblem)
        {
            return Problems.Invalid(mappingProblem);
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var file = await tx.QuerySingleOrDefaultAsync<FileGate>(
                    "SELECT site_id AS SiteId, status AS Status, parser_status AS ParserStatus FROM hub.import_files WHERE id = @id",
                    new { id });

                if (file?.SiteId is not { } site || !http.GetScope().Includes(site))
                {
                    return Problems.NotFound();
                }

                if (file.Status != "inspected")
                {
                    return Problems.Conflict("O arquivo ainda não terminou a inspeção.");
                }

                // Sem interpretacao pela documentacao, so com mapeamento feito por uma pessoa. Ilegivel nao tem o que mapear.
                if (file.ParserStatus == ParserStatuses.Unreadable)
                {
                    return Problems.Of(StatusCodes.Status422UnprocessableEntity, "file_unreadable", "Este arquivo não pôde ser lido. Corrija-o e envie de novo.");
                }

                if (file.ParserStatus != ParserStatuses.ParsedByDocumentation && body.Mapping is null)
                {
                    return Problems.Of(StatusCodes.Status422UnprocessableEntity, "mapping_required", "Indique como as colunas deste arquivo correspondem ao cadastro.");
                }

                if (body.DeviceId is { } device
                    && await tx.QuerySingleOrDefaultAsync<int?>("SELECT 1 FROM hub.devices WHERE id = @device AND site_id = @site", new { device, site }) is null)
                {
                    return Problems.Invalid("O equipamento informado não pertence à unidade deste arquivo.");
                }

                // Previa vencida nao pode travar uma nova tentativa.
                await tx.ExecuteAsync(
                    "UPDATE hub.import_runs SET status = 'expired', completed_at = now() WHERE file_id = @id AND status IN ('preview_ready', 'awaiting_approval') AND expires_at < now()",
                    new { id });

                var runId = await tx.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    INSERT INTO hub.import_runs (tenant_id, file_id, status, dry_run, created_by, device_id, mapping, field_policy, source_system)
                    VALUES (@TenantId, @id, 'queued', true, @actor, @device, @mapping::jsonb, @policy::jsonb, @system)
                    ON CONFLICT (tenant_id, file_id) WHERE status IN ('queued', 'processing', 'preview_ready', 'awaiting_approval') DO NOTHING
                    RETURNING id
                    """,
                    new
                    {
                        tx.TenantId,
                        id,
                        actor = access.Actor.ActorId,
                        device = body.DeviceId,
                        system = body.SourceSystem,
                        mapping = body.Mapping is null ? null : JsonSerializer.Serialize(body.Mapping, Json),
                        policy = JsonSerializer.Serialize(body.FieldPolicy ?? [], Json),
                    });

                if (runId is not { } run)
                {
                    return Problems.Conflict("Já existe uma prévia em andamento para este arquivo.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.run.create", "import_run", run.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { file = id }));
                var row = await FindAsync(tx, run);
                return Results.Created($"/api/v1/import-runs/{run}", ToResponse(row!));
            },
            cancellationToken);
    }

    private static async Task<IResult> RunsOfFileAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<RunRow>(
                $"SELECT {RunColumns} FROM {FromRun} WHERE r.file_id = @id AND {ScopeFilter} ORDER BY r.created_at DESC, r.id LIMIT 50",
                new { id, all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() }),
            cancellationToken);
        return Results.Ok(rows.Select(ToResponse));
    }

    private static async Task<IResult> HistoryAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<RunRow>(
                $"SELECT {RunColumns} FROM {FromRun} WHERE {ScopeFilter} ORDER BY r.created_at DESC, r.id LIMIT 200",
                new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() }),
            cancellationToken);
        return Results.Ok(rows.Select(ToResponse));
    }

    private static async Task<IResult> PendingApprovalsAsync(HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var scope = http.GetScope();
        var rows = await database.InTenantAsync(
            access.Actor.TenantId,
            tx => tx.QueryAsync<RunRow>(
                $"SELECT {RunColumns} FROM {FromRun} WHERE r.status = 'awaiting_approval' AND r.expires_at > now() AND {ScopeFilter} ORDER BY r.submitted_at, r.id LIMIT 200",
                new { all = scope.AllSites, allowed = scope.AllowedSites.ToArray(), denied = scope.DeniedSites.ToArray() }),
            cancellationToken);
        return Results.Ok(rows.Select(ToResponse));
    }

    private static async Task<IResult> GetRunAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var run = await database.InTenantAsync(access.Actor.TenantId, tx => FindAsync(tx, id), cancellationToken);
        return run is null || !InScope(http, run) ? Problems.NotFound() : Results.Ok(ToResponse(run));
    }

    private static async Task<IResult> RowsAsync(Guid id, string? kind, long? cursor, int? limit, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var run = await FindAsync(tx, id);
                if (run is null || !InScope(http, run))
                {
                    return Problems.NotFound();
                }

                var rows = (await tx.QueryAsync<RowRow>(
                    """
                    SELECT x.line_no AS LineNo, x.source_object AS SourceObject, x.source_key AS SourceKey, x.result_kind AS Kind, x.match_person_id AS MatchPersonId,
                           array_to_string(x.error_codes, ',') AS CodesText, x.sanitized_preview::text AS SampleText, d.decision AS Decision, d.person_id AS DecisionPersonId
                    FROM hub.import_rows x
                    LEFT JOIN hub.import_decisions d ON d.tenant_id = x.tenant_id AND d.run_id = x.run_id AND d.line_no = x.line_no AND d.source_object = x.source_object
                    WHERE x.run_id = @id AND (@kind::text IS NULL OR x.result_kind = @kind) AND x.line_no > @after
                    ORDER BY x.line_no, x.source_object
                    LIMIT @take
                    """,
                    new { id, kind, after = cursor ?? 0, take = size + 1 })).ToList();

                var page = rows.Take(size).Select(r => new ImportRowResponse(
                    r.LineNo,
                    r.SourceObject,
                    r.SourceKey,
                    r.Kind,
                    r.MatchPersonId,
                    r.CodesText.Split(',', StringSplitOptions.RemoveEmptyEntries),
                    r.SampleText is null or "{}" ? null : JsonSerializer.Deserialize<PreviewSample>(r.SampleText, Json),
                    r.Decision,
                    r.DecisionPersonId)).ToList();
                return Results.Ok(new ImportRowPageResponse(page, rows.Count > size ? page[^1].LineNo : null));
            },
            cancellationToken);
    }

    /// <summary>Linha, objeto e motivo. Nenhum valor do arquivo: esta planilha circula, e codigo nao executa como formula.</summary>
    private static async Task<IResult> ErrorsAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var run = await FindAsync(tx, id);
                if (run is null || !InScope(http, run))
                {
                    return Problems.NotFound();
                }

                var rows = await tx.QueryAsync<ErrorRow>(
                    "SELECT line_no AS LineNo, source_object AS SourceObject, array_to_string(error_codes, ' ') AS Codes FROM hub.import_rows WHERE run_id = @id AND result_kind = 'invalid' ORDER BY line_no, source_object",
                    new { id });

                var csv = new StringBuilder("linha;objeto;motivos\r\n");
                foreach (var row in rows)
                {
                    csv.Append(row.LineNo).Append(';').Append(row.SourceObject).Append(';').Append(row.Codes).Append("\r\n");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.errors_export", "import_run", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"erros-{id}.csv");
            },
            cancellationToken);
    }

    private static async Task<IResult> DecideAsync(Guid id, ImportDecisionRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.Decision is not (ImportDecisions.AcceptSource or ImportDecisions.KeepPlatform or ImportDecisions.Link or ImportDecisions.CreateNew or ImportDecisions.Skip))
        {
            return Problems.Invalid("Decisão inválida.");
        }

        var lines = body.Lines?.Distinct().ToArray();
        if (lines is { Length: > MaxDecisionLines })
        {
            return Problems.Invalid($"Informe até {MaxDecisionLines} linhas por vez.");
        }

        if ((lines is null or { Length: 0 }) == (body.Kind is null))
        {
            return Problems.Invalid("Informe as linhas ou a categoria, um dos dois.");
        }

        if ((body.Decision == ImportDecisions.Link) != (body.PersonId is not null))
        {
            return Problems.Invalid("Vincular exige escolher a pessoa, e só vincular aceita pessoa.");
        }

        // Vincular em lote juntaria pessoas diferentes numa so.
        if (body.Decision == ImportDecisions.Link && lines is not { Length: 1 })
        {
            return Problems.Invalid("Vincule uma linha por vez.");
        }

        if (body.Kind is not null && !PreviewPlan.RequiresDecision(body.Kind))
        {
            return Problems.Invalid("Esta categoria não pede decisão.");
        }

        // Em lote, criar pessoa nova resolveria suspeitas de duplicidade sem ninguem olhar cada uma.
        if (body.Kind is not null && (!PreviewPlan.Allows(body.Kind, body.Decision) || (body.Kind == PreviewKinds.PossibleDuplicate && body.Decision == ImportDecisions.CreateNew)))
        {
            return Problems.Invalid("Esta decisão não vale para toda a categoria.");
        }

        try
        {
            return await ApplyDecisionAsync(id, body, lines, http, access, database, cancellationToken);
        }
        catch (DecisionMismatchException)
        {
            return Problems.Of(StatusCodes.Status422UnprocessableEntity, "decision_not_allowed", "Esta decisão não vale para uma das linhas escolhidas. Nada foi registrado.");
        }
    }

    private static Task<IResult> ApplyDecisionAsync(Guid id, ImportDecisionRequest body, long[]? lines, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var run = await FindAsync(tx, id);
                if (run is null || !InScope(http, run))
                {
                    return Problems.NotFound();
                }

                if (run.Status != "preview_ready" || IsExpired(run))
                {
                    return Problems.Conflict("Esta prévia não aceita mais decisões.");
                }

                var kinds = new[] { PreviewKinds.FieldConflict, PreviewKinds.PossibleDuplicate }.Where(k => PreviewPlan.Allows(k, body.Decision!)).ToArray();

                // O vinculo so aceita quem a previa ja ofereceu como candidato: nada de pessoa de outra unidade.
                var applied = await tx.ExecuteAsync(
                    """
                    INSERT INTO hub.import_decisions (tenant_id, run_id, line_no, source_object, decision, person_id, decided_by)
                    SELECT x.tenant_id, x.run_id, x.line_no, x.source_object, @decision, @person, @actor
                    FROM hub.import_rows x
                    WHERE x.run_id = @id
                      AND x.result_kind = ANY(@kinds)
                      AND (x.line_no = ANY(@lines) OR x.result_kind = @kind)
                      AND NOT (@decision = 'accept_source' AND @otherPerson = ANY(x.error_codes))
                      AND (@person::uuid IS NULL OR x.sanitized_preview -> 'candidates' @> jsonb_build_array(jsonb_build_object('personId', @person::text)))
                    ON CONFLICT (tenant_id, run_id, line_no, source_object)
                    DO UPDATE SET decision = EXCLUDED.decision, person_id = EXCLUDED.person_id, decided_by = EXCLUDED.decided_by, decided_at = now()
                    """,
                    new { decision = body.Decision, person = body.PersonId, actor = access.Actor.ActorId, id, kinds, lines = lines ?? [], kind = body.Kind, otherPerson = PreviewCodes.CredentialOfOtherPerson });

                if (lines is { Length: > 0 } && applied != lines.Length)
                {
                    throw new DecisionMismatchException();
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.decision", "import_run", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { decision = body.Decision, count = applied }));
                return Results.Ok(ToResponse((await FindAsync(tx, id))!));
            },
            cancellationToken);

    private static async Task<IResult> SubmitAsync(Guid id, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var run = await FindAsync(tx, id);
                if (run is null || !InScope(http, run))
                {
                    return Problems.NotFound();
                }

                if (run.Status != "preview_ready" || IsExpired(run))
                {
                    return Problems.Conflict("Esta prévia não pode mais ser enviada para aprovação.");
                }

                if (run.UnresolvedDecisions > 0)
                {
                    return Problems.Of(StatusCodes.Status422UnprocessableEntity, "decisions_pending", $"Faltam {run.UnresolvedDecisions} decisões antes de enviar para aprovação.");
                }

                var (plan, digest) = await ComputePlanAsync(tx, run);
                var updated = await tx.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    UPDATE hub.import_runs SET status = 'awaiting_approval', plan = @plan::jsonb, preview_digest = @digest, submitted_by = @actor, submitted_at = now()
                    WHERE id = @id AND status = 'preview_ready'
                    RETURNING id
                    """,
                    new { plan = JsonSerializer.Serialize(plan, Json), digest, actor = access.Actor.ActorId, id });

                if (updated is null)
                {
                    return Problems.Conflict("Esta prévia não pode mais ser enviada para aprovação.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.submit", "import_run", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { digest }));
                return Results.Ok(ToResponse((await FindAsync(tx, id))!));
            },
            cancellationToken);
    }

    private static async Task<IResult> ApproveAsync(Guid id, ApproveImportRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, ImportApprovalOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(body.PreviewDigest))
        {
            return Problems.Invalid("Informe o resumo da prévia que foi conferida.");
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var run = await FindAsync(tx, id);
                if (run is null || !InScope(http, run))
                {
                    return Problems.NotFound();
                }

                if (run.Status != "awaiting_approval")
                {
                    return Problems.Conflict("Esta importação não está aguardando aprovação.");
                }

                if (IsExpired(run))
                {
                    await tx.ExecuteAsync("UPDATE hub.import_runs SET status = 'expired', completed_at = now() WHERE id = @id AND status = 'awaiting_approval'", new { id });
                    return Problems.Of(StatusCodes.Status409Conflict, "preview_expired", "Esta prévia venceu. Gere uma nova para conferir os dados atuais.");
                }

                // O que se aprova e exatamente o que se viu.
                if (!string.Equals(run.PreviewDigest, body.PreviewDigest, StringComparison.Ordinal))
                {
                    return Problems.Of(StatusCodes.Status409Conflict, "preview_changed", "A prévia mudou depois que você a abriu. Confira de novo antes de aprovar.");
                }

                var plan = JsonSerializer.Deserialize<ImportPlan>(run.PlanText!, Json)!;
                if (plan.PendingReferences > 0 && !body.AcknowledgePendingReferences)
                {
                    return Problems.Of(StatusCodes.Status422UnprocessableEntity, "pending_references", "Há referências sem destino no arquivo. Confirme que está ciente antes de aprovar.");
                }

                var preparedByApprover = run.CreatedBy == access.Actor.ActorId || run.SubmittedBy == access.Actor.ActorId;
                if (preparedByApprover && plan.Add + plan.Update >= options.DualApprovalThreshold)
                {
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.approve", "import_run", id.ToString(), AuditWriter.Denied, http.GetCorrelationId(), Metadata: new { reason = "second_approver_required" }));
                    return Problems.Forbidden("second_approver_required", $"Com {options.DualApprovalThreshold} alterações ou mais, outra pessoa precisa aprovar.");
                }

                var updated = await tx.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    UPDATE hub.import_runs SET status = 'approved', approved_by = @actor, approved_at = now()
                    WHERE id = @id AND status = 'awaiting_approval' AND preview_digest = @digest
                    RETURNING id
                    """,
                    new { actor = access.Actor.ActorId, id, digest = body.PreviewDigest });

                if (updated is null)
                {
                    return Problems.Conflict("Esta importação não está aguardando aprovação.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.approve", "import_run", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Metadata: new { digest = body.PreviewDigest, plan.Add, plan.Update }));
                return Results.Ok(ToResponse((await FindAsync(tx, id))!));
            },
            cancellationToken);
    }

    private static Task<IResult> RejectAsync(Guid id, CloseImportRunRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        CloseAsync(id, body, "rejected", "import.reject", ["awaiting_approval"], http, access, database, cancellationToken);

    private static Task<IResult> CancelAsync(Guid id, CloseImportRunRequest body, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken) =>
        CloseAsync(id, body, "cancelled", "import.cancel", ["queued", "processing", "preview_ready", "awaiting_approval"], http, access, database, cancellationToken);

    private static async Task<IResult> CloseAsync(Guid id, CloseImportRunRequest body, string status, string action, string[] from, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (body.Reason is { Length: > MaxReason })
        {
            return Problems.Invalid($"O motivo deve ter até {MaxReason} caracteres.");
        }

        return await database.InTenantAsync<IResult>(
            access.Actor.TenantId,
            async tx =>
            {
                var run = await FindAsync(tx, id);
                if (run is null || !InScope(http, run))
                {
                    return Problems.NotFound();
                }

                var updated = await tx.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    UPDATE hub.import_runs SET status = @status, closed_by = @actor, closed_at = now(), close_reason = @reason, completed_at = now()
                    WHERE id = @id AND status = ANY(@from)
                    RETURNING id
                    """,
                    new { status, actor = access.Actor.ActorId, reason = body.Reason, id, from });

                if (updated is null)
                {
                    return Problems.Conflict("O estado atual desta importação não permite esta ação.");
                }

                await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, action, "import_run", id.ToString(), AuditWriter.Success, http.GetCorrelationId(), Reason: body.Reason));
                return Results.Ok(ToResponse((await FindAsync(tx, id))!));
            },
            cancellationToken);
    }

    /// <summary>Le os nomes e amostras mascaradas do original cifrado, para a tela de mapeamento. Nada disso e gravado.</summary>
    private static async Task<IResult> ColumnsAsync(Guid id, string? delimiter, bool? hasHeader, HttpContext http, RequestAccess access, ITenantDatabase database, CancellationToken cancellationToken)
    {
        if (http.RequestServices.GetService<EncryptedBlobStore>() is not { } store)
        {
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, "storage_unavailable", "Armazenamento indisponível.");
        }

        var tenant = access.Actor.TenantId;
        var file = await database.InTenantAsync(
            tenant,
            tx => tx.QuerySingleOrDefaultAsync<ColumnsGate>(
                "SELECT site_id AS SiteId, status AS Status, detected_type AS DetectedType, encrypted_blob_ref AS BlobRef, content_size_bytes AS Size FROM hub.import_files WHERE id = @id",
                new { id }),
            cancellationToken);

        if (file?.SiteId is not { } site || !http.GetScope().Includes(site))
        {
            return Problems.NotFound();
        }

        if (file.Status != "inspected" || file.DetectedType is null || !Guid.TryParse(file.BlobRef, out var blob))
        {
            return Problems.Conflict("O arquivo ainda não terminou a inspeção.");
        }

        await using Stream plain = (file.Size ?? 0) <= 64 * 1024 * 1024
            ? new MemoryStream()
            : new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        try
        {
            if (!await store.ReadAsync(tenant, blob, plain, cancellationToken))
            {
                return Problems.Of(StatusCodes.Status410Gone, "original_unavailable", "O arquivo original não está mais disponível.");
            }
        }
        catch (Exception ex) when (ex is KeyServiceException or IOException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonClientException)
        {
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, "storage_unavailable", "Armazenamento indisponível.");
        }

        using var csv = ImportCsvSource.For(plain, file.DetectedType, cancellationToken);
        if (csv is null)
        {
            return Problems.Of(StatusCodes.Status422UnprocessableEntity, "mapping_not_available", "Este pacote não tem um único CSV para mapear.");
        }

        var limits = new CsvLimits();
        try
        {
            System.Text.Encoding encoding;
            await using (var stream = await csv.OpenAsync())
            {
                (encoding, _) = await SafeCsvReader.DetectEncodingAsync(stream, cancellationToken);
            }

            char chosen;
            if (delimiter is not null)
            {
                if (TabularMappingRules.DelimiterOf(delimiter) is not { } parsed)
                {
                    return Problems.Invalid("Separador inválido: use ponto e vírgula, vírgula ou tabulação.");
                }

                chosen = parsed;
            }
            else
            {
                using var detect = new StreamReader(await csv.OpenAsync(), encoding, detectEncodingFromByteOrderMarks: false);
                chosen = await SafeCsvReader.DetectDelimiterAsync(detect, limits, cancellationToken);
            }

            using var reader = new StreamReader(await csv.OpenAsync(), encoding, detectEncodingFromByteOrderMarks: false);
            var rows = new List<IReadOnlyList<string>>();
            await foreach (var row in SafeCsvReader.ReadRowsAsync(reader, chosen, limits, cancellationToken))
            {
                rows.Add(row);
                if (rows.Count > SampleRows)
                {
                    break;
                }
            }

            var header = hasHeader ?? true;
            var width = rows.Count == 0 ? 0 : rows[0].Count;
            var data = rows.Skip(header ? 1 : 0).Take(SampleRows).ToList();
            var columns = Enumerable.Range(0, width).Select(i => new ImportColumnResponse(
                i,
                header ? rows[0][i] : null,
                data.Where(r => i < r.Count).Select(r => Mask.Code(r[i])).ToList())).ToList();

            await database.InTenantAsync(
                tenant,
                async tx =>
                {
                    await AuditWriter.WriteAsync(tx, new AuditEntry(access.Actor.ActorId, "import.columns_read", "import_file", id.ToString(), AuditWriter.Success, http.GetCorrelationId()));
                    return true;
                },
                cancellationToken);

            return Results.Ok(new ImportColumnsResponse(chosen == '\t' ? "tab" : chosen.ToString(), columns));
        }
        catch (CsvFormatException ex) when (ex.Code == CsvErrors.AmbiguousDelimiter)
        {
            return Problems.Of(StatusCodes.Status422UnprocessableEntity, "delimiter_required", "Não foi possível identificar o separador. Escolha um.");
        }
        catch (CsvFormatException)
        {
            return Problems.Of(StatusCodes.Status422UnprocessableEntity, "file_unreadable", "Este arquivo não pôde ser lido com esse separador.");
        }
        catch (InvalidDataException)
        {
            return Problems.Of(StatusCodes.Status422UnprocessableEntity, "file_unreadable", "Este arquivo não pôde ser lido.");
        }
    }

    private static async Task<(ImportPlan Plan, string Digest)> ComputePlanAsync(TenantTransaction tx, RunRow run)
    {
        var rows = (await tx.QueryAsync<PlanRow>(
            """
            SELECT x.line_no AS LineNo, x.source_object AS SourceObject, x.result_kind AS Kind, encode(x.row_digest, 'hex') AS RowDigest, d.decision AS Decision, d.person_id AS PersonId
            FROM hub.import_rows x
            LEFT JOIN hub.import_decisions d ON d.tenant_id = x.tenant_id AND d.run_id = x.run_id AND d.line_no = x.line_no AND d.source_object = x.source_object
            WHERE x.run_id = @Id
            ORDER BY x.line_no, x.source_object
            """,
            new { run.Id })).ToList();

        long add = 0, update = 0, unchanged = 0, skipped = 0, invalid = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{run.Id}|{run.StatsText}|"));
        foreach (var row in rows)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{row.LineNo}|{row.SourceObject}|{row.Kind}|{row.RowDigest}|{row.Decision}|{row.PersonId}\n"));
            switch (row.Kind, row.Decision)
            {
                case (PreviewKinds.New, _):
                case (PreviewKinds.PossibleDuplicate, ImportDecisions.CreateNew):
                    add++;
                    break;
                case (PreviewKinds.Update, _):
                case (PreviewKinds.FieldConflict, ImportDecisions.AcceptSource):
                case (PreviewKinds.PossibleDuplicate, ImportDecisions.Link):
                    update++;
                    break;
                case (PreviewKinds.NoChange, _):
                case (PreviewKinds.FieldConflict, ImportDecisions.KeepPlatform):
                    unchanged++;
                    break;
                case (PreviewKinds.Invalid, _):
                    invalid++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        var stats = JsonSerializer.Deserialize<PreviewStats>(run.StatsText!, Json)!;
        var plan = new ImportPlan(
            add,
            update,
            unchanged,
            skipped,
            invalid + stats.InvalidRowsNotStored,
            stats.PendingReferences.Values.Sum(),
            stats.BiometricsExcluded,
            stats.Credentials.Values.Sum(c => c.New),
            stats.CredentialsNotCompared.Values.Sum(),
            stats.Events.New,
            stats.Events.NotCompared);
        return (plan, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool IsExpired(RunRow run) => run.ExpiresAt is { } expires && DateTime.SpecifyKind(expires, DateTimeKind.Utc) <= DateTime.UtcNow;

    private static bool InScope(HttpContext http, RunRow run) => run.SiteId is { } site && http.GetScope().Includes(site);

    private static Task<RunRow?> FindAsync(TenantTransaction tx, Guid id) =>
        tx.QuerySingleOrDefaultAsync<RunRow>($"SELECT {RunColumns} FROM {FromRun} WHERE r.id = @id", new { id });

    private static ImportRunResponse ToResponse(RunRow r) => new(
        r.Id,
        r.FileId,
        r.SiteId,
        r.OriginalName,
        r.SourceFormat,
        r.Status,
        r.DryRun,
        r.ErrorCode,
        r.DeviceId,
        r.ParserId,
        r.StatsText is null or "{}" ? null : JsonSerializer.Deserialize<PreviewStats>(r.StatsText, Json),
        r.PlanText is null ? null : JsonSerializer.Deserialize<ImportPlan>(r.PlanText, Json),
        r.PreviewDigest,
        r.UnresolvedDecisions,
        r.CreatedBy,
        r.CreatedAt,
        r.ExpiresAt,
        r.SubmittedBy,
        r.SubmittedAt,
        r.ApprovedBy,
        r.ApprovedAt,
        r.ClosedBy,
        r.ClosedAt,
        r.CloseReason);
}

internal sealed class DecisionMismatchException : Exception
{
    public DecisionMismatchException()
        : base("A decisao nao vale para todas as linhas.")
    {
    }

    public DecisionMismatchException(string message)
        : base(message)
    {
    }

    public DecisionMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record FileGate(Guid? SiteId, string Status, string? ParserStatus);

internal sealed record ColumnsGate(Guid? SiteId, string Status, string? DetectedType, string? BlobRef, long? Size);

internal sealed record PlanRow(long LineNo, string SourceObject, string Kind, string RowDigest, string? Decision, Guid? PersonId);

internal sealed record ErrorRow(long LineNo, string SourceObject, string Codes);

internal sealed record RowRow(long LineNo, string SourceObject, string? SourceKey, string Kind, Guid? MatchPersonId, string CodesText, string? SampleText, string? Decision, Guid? DecisionPersonId);

internal sealed record RunRow(
    Guid Id,
    Guid FileId,
    Guid? SiteId,
    string? OriginalName,
    string? SourceFormat,
    string Status,
    bool DryRun,
    string? ErrorCode,
    Guid? DeviceId,
    string? ParserId,
    string? StatsText,
    string? PlanText,
    string? PreviewDigest,
    long UnresolvedDecisions,
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    Guid? SubmittedBy,
    DateTime? SubmittedAt,
    Guid? ApprovedBy,
    DateTime? ApprovedAt,
    Guid? ClosedBy,
    DateTime? ClosedAt,
    string? CloseReason);
