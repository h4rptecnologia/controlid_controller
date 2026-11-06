using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Modules.Importation;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests;

/// <summary>Do envio a aprovacao, com banco, armazenamento e KMS reais e os papeis de cada processo. So o antivirus e simulado.</summary>
[Collection(PostgresTests.Name)]
public sealed class ImportRunTests(PostgresFixture postgres, StorageFixture storage) : IDisposable
{
    private readonly TestData data = new(postgres.AdminOnHubConnection);
    private readonly EdgeHost host = new(postgres, storage);
    private readonly HttpClient kms = storage.CreateKmsClient();
    private readonly Amazon.S3.IAmazonS3 s3 = storage.CreateS3Client();
    private readonly NpgsqlDataSource workerSource = NpgsqlDataSource.Create(postgres.WorkerImportConnection);

    public void Dispose()
    {
        kms.Dispose();
        s3.Dispose();
        workerSource.Dispose();
        host.Dispose();
    }

    private static CancellationToken None => CancellationToken.None;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private EncryptedBlobStore Store() =>
        new(new S3ObjectStore(s3, StorageFixture.Bucket), new TransitKeyService(kms), new TenantDatabase(workerSource));

    private async Task DrainAsync(PreviewLimits? limits = null)
    {
        var inspection = new ImportInspectionService(workerSource, new TenantDatabase(workerSource), Store(), new FakeScanner(new ScanResult(ScanVerdict.Clean)), new InspectionLimits(), NullLogger<ImportInspectionService>.Instance);
        for (var i = 0; i < 25 && await inspection.ProcessNextAsync(None); i++)
        {
        }

        var previews = new ImportPreviewService(workerSource, new TenantDatabase(workerSource), Store(), new CredentialProtector(new TransitKeyService(kms)), limits ?? new PreviewLimits(), NullLogger<ImportPreviewService>.Instance);
        for (var i = 0; i < 25 && await previews.ProcessNextAsync(None); i++)
        {
        }
    }

    private sealed record Team(HttpClient Operator, HttpClient Approver, Guid Site, Guid Tenant, string Slug, TestActor OperatorActor);

    private async Task<Team> TeamAsync()
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var actor = await data.ActorAsync(tenant, Permissions.ImportCreate);
        return new Team(host.AdminClient(actor), host.AdminClient(await data.ActorAsync(tenant, Permissions.ImportApprove)), site, tenant.Id, tenant.Slug, actor);
    }

    private static byte[] Zip(string csvName, string csv)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry(csvName).Open();
            stream.Write(Encoding.UTF8.GetBytes(csv));
        }

        return buffer.ToArray();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid site, byte[] content)
    {
        var created = await client.PostAsJsonAsync(Url("/api/v1/imports"), new { siteId = site, fileName = "arquivo", sizeBytes = content.Length });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(Url($"/api/v1/imports/{id}/content"), new ByteArrayContent(content))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url($"/api/v1/imports/{id}/complete-upload"), new { sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) })).StatusCode);
        return id;
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid file, object? body = null) =>
        client.PostAsJsonAsync(Url($"/api/v1/imports/{file}/runs"), body ?? new { });

    private async Task<Guid> PreviewAsync(Team team, string csv, object? body = null, string name = "users.csv")
    {
        var file = await UploadAsync(team.Operator, team.Site, Zip(name, csv));
        await DrainAsync();
        var started = await StartAsync(team.Operator, file, body);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();
        return run;
    }

    private static async Task<JsonElement> RunAsync(HttpClient client, Guid run) =>
        await (await client.GetAsync(Url($"/api/v1/import-runs/{run}"))).Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<List<JsonElement>> RowsAsync(HttpClient client, Guid run, string? kind = null)
    {
        var page = await (await client.GetAsync(Url($"/api/v1/import-runs/{run}/rows{(kind is null ? string.Empty : $"?kind={kind}")}"))).Content.ReadFromJsonAsync<JsonElement>();
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    private static long People(JsonElement run, string kind) =>
        run.GetProperty("stats").GetProperty("people").TryGetProperty(kind, out var value) ? value.GetInt64() : 0;

    private async Task<Guid> PersonAsync(Guid tenant, string name, string registration, Guid? site)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO hub.people (tenant_id, site_id, display_name, registration) VALUES (@t, @s, @n, @r) RETURNING id", connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("s", site is null ? DBNull.Value : site);
        command.Parameters.AddWithValue("n", name);
        command.Parameters.AddWithValue("r", registration);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(Guid tenant, string table)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM hub.{table} WHERE tenant_id = @t", connection);
        command.Parameters.AddWithValue("t", tenant);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid run, object body) =>
        client.PostAsJsonAsync(Url($"/api/v1/import-runs/{run}/decisions"), body);

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid run) =>
        client.PostAsJsonAsync(Url($"/api/v1/import-runs/{run}/submit"), new { });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid run, string? digest, bool acknowledge = false) =>
        client.PostAsJsonAsync(Url($"/api/v1/import-approvals/{run}/approve"), new { previewDigest = digest, acknowledgePendingReferences = acknowledge });

    private static async Task<string> SubmittedDigestAsync(HttpClient client, Guid run)
    {
        var response = await SubmitAsync(client, run);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("awaiting_approval", body.GetProperty("status").GetString());
        return body.GetProperty("previewDigest").GetString()!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static long[] LineOf(long line) => [line];

    private static readonly long[] Line1 = [1];
    private static readonly long[] Line3 = [3];
    private static readonly long[] Line4 = [4];
    private static readonly long[] Lines34 = [3, 4];

    private const string TwoUsers = "users\nid;registration;name\n1;MAT001;Ana Souza\n2;MAT002;Bruno Lima\n";

    // Caminho completo

    [Fact]
    public async Task Native_file_goes_from_upload_to_approval_without_touching_the_registry()
    {
        var team = await TeamAsync();
        var before = await CountAsync(team.Tenant, "people");

        var run = await PreviewAsync(team, TwoUsers);

        var preview = await RunAsync(team.Operator, run);
        Assert.Equal("preview_ready", preview.GetProperty("status").GetString());
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, People(preview, PreviewKinds.New));
        Assert.Equal(NativeCsvParser.ParserId, preview.GetProperty("parserId").GetString());

        var digest = await SubmittedDigestAsync(team.Operator, run);
        var approved = await ApproveAsync(team.Approver, run, digest);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("plan").GetProperty("add").GetInt64());

        // Aprovar nao e importar: o cadastro so muda quando a aplicacao existir.
        Assert.Equal(before, await CountAsync(team.Tenant, "people"));
        Assert.Equal(0, await CountAsync(team.Tenant, "commands"));
    }

    [Fact]
    public async Task Existing_person_with_same_data_is_no_change_so_reimporting_does_not_duplicate()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Ana Souza", "MAT001", team.Site);
        await PersonAsync(team.Tenant, "Bruno Lima", "MAT002", team.Site);

        var preview = await RunAsync(team.Operator, await PreviewAsync(team, TwoUsers));

        Assert.Equal(2, People(preview, PreviewKinds.NoChange));
        Assert.Equal(0, People(preview, PreviewKinds.New));
    }

    [Fact]
    public async Task Same_file_sent_twice_gives_the_same_classification()
    {
        var team = await TeamAsync();
        var first = await RunAsync(team.Operator, await PreviewAsync(team, TwoUsers));
        var second = await RunAsync(team.Operator, await PreviewAsync(team, TwoUsers));

        Assert.Equal(first.GetProperty("stats").GetRawText(), second.GetProperty("stats").GetRawText());
    }

    [Fact]
    public async Task Registration_in_another_site_is_an_explicit_conflict_without_identity()
    {
        var team = await TeamAsync();
        var otherSite = await data.SiteAsync(team.Tenant, "Outra");
        var person = await PersonAsync(team.Tenant, "MARCADOR Outra Unidade", "MAT001", otherSite);

        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n");

        var row = (await RowsAsync(team.Operator, run)).Single();
        Assert.Equal(PreviewKinds.PossibleDuplicate, row.GetProperty("kind").GetString());
        Assert.Contains(PreviewCodes.RegistrationInOtherSite, row.GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
        Assert.DoesNotContain(person.ToString(), row.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MARCADOR", row.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_tenant_person_is_never_considered()
    {
        var team = await TeamAsync();
        var other = await TeamAsync();
        await PersonAsync(other.Tenant, "Ana Souza", "MAT001", other.Site);

        var preview = await RunAsync(team.Operator, await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n"));

        Assert.Equal(1, People(preview, PreviewKinds.New));
    }

    // Decisoes

    [Fact]
    public async Task Conflict_blocks_submission_until_decided()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n");

        var blocked = await SubmitAsync(team.Operator, run);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("decisions_pending", await CodeAsync(blocked));

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.AcceptSource })).StatusCode);

        var after = await RunAsync(team.Operator, run);
        Assert.Equal(0, after.GetProperty("unresolvedDecisions").GetInt64());
        var body = await (await SubmitAsync(team.Operator, run)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("plan").GetProperty("update").GetInt64());
    }

    [Fact]
    public async Task Keeping_the_platform_value_counts_as_unchanged_and_skip_as_skipped()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        await PersonAsync(team.Tenant, "Outro Antigo", "MAT002", team.Site);
        var run = await PreviewAsync(team, TwoUsers);

        await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.KeepPlatform });
        await DecideAsync(team.Operator, run, new { lines = Line4, decision = ImportDecisions.Skip });

        var plan = (await (await SubmitAsync(team.Operator, run)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan");
        Assert.Equal(1, plan.GetProperty("unchanged").GetInt64());
        Assert.Equal(1, plan.GetProperty("skipped").GetInt64());
        Assert.Equal(0, plan.GetProperty("update").GetInt64());
    }

    [Fact]
    public async Task Link_accepts_only_a_person_offered_as_candidate()
    {
        var team = await TeamAsync();
        var first = await PersonAsync(team.Tenant, "Ana A", "MAT001", team.Site);
        await PersonAsync(team.Tenant, "Ana B", "MAT001", null);
        var stranger = await PersonAsync(team.Tenant, "Fora Da Lista", "ZZZ", team.Site);
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n");

        var refused = await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.Link, personId = stranger });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("decision_not_allowed", await CodeAsync(refused));

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.Link, personId = first })).StatusCode);
        var row = (await RowsAsync(team.Operator, run)).Single();
        Assert.Equal(first, row.GetProperty("decisionPersonId").GetGuid());
    }

    [Fact]
    public async Task Batch_with_one_wrong_line_records_nothing()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n2;MAT002;Bruno Lima\n");

        var response = await DecideAsync(team.Operator, run, new { lines = Lines34, decision = ImportDecisions.AcceptSource });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1, (await RunAsync(team.Operator, run)).GetProperty("unresolvedDecisions").GetInt64());
    }

    [Fact]
    public async Task Whole_category_can_be_resolved_at_once()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        await PersonAsync(team.Tenant, "Outro Antigo", "MAT002", team.Site);
        var run = await PreviewAsync(team, TwoUsers);

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(team.Operator, run, new { kind = PreviewKinds.FieldConflict, decision = ImportDecisions.AcceptSource })).StatusCode);

        Assert.Equal(0, (await RunAsync(team.Operator, run)).GetProperty("unresolvedDecisions").GetInt64());
    }

    [Theory]
    [InlineData(PreviewKinds.PossibleDuplicate, ImportDecisions.CreateNew)]
    [InlineData(PreviewKinds.New, ImportDecisions.Skip)]
    [InlineData(PreviewKinds.FieldConflict, ImportDecisions.CreateNew)]
    public async Task Bulk_decision_that_would_hide_a_review_is_refused(string kind, string decision)
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(team.Operator, run, new { kind, decision })).StatusCode);
    }

    // Aprovacao

    [Fact]
    public async Task Approval_requires_the_digest_of_what_was_seen()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        var digest = await SubmittedDigestAsync(team.Operator, run);

        var wrong = await ApproveAsync(team.Approver, run, new string('0', 64));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("preview_changed", await CodeAsync(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, (await ApproveAsync(team.Approver, run, null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(team.Approver, run, digest)).StatusCode);
    }

    [Fact]
    public async Task Different_decisions_produce_a_different_digest()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        var csv = "users\nid;registration;name\n1;MAT001;Ana Souza\n";

        var first = await PreviewAsync(team, csv);
        await DecideAsync(team.Operator, first, new { lines = Line3, decision = ImportDecisions.AcceptSource });
        var firstDigest = await SubmittedDigestAsync(team.Operator, first);
        await team.Operator.PostAsJsonAsync(Url($"/api/v1/import-runs/{first}/cancel"), new { });

        var second = await PreviewAsync(team, csv);
        await DecideAsync(team.Operator, second, new { lines = Line3, decision = ImportDecisions.KeepPlatform });

        Assert.NotEqual(firstDigest, await SubmittedDigestAsync(team.Operator, second));
    }

    [Fact]
    public async Task Decisions_stop_after_submission()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n");
        await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.AcceptSource });
        await SubmittedDigestAsync(team.Operator, run);

        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.Skip })).StatusCode);
    }

    [Fact]
    public async Task Pending_references_need_an_explicit_acknowledgement()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\ncards\nid;value;user_id\n5;42;99\n");
        var digest = await SubmittedDigestAsync(team.Operator, run);

        var refused = await ApproveAsync(team.Approver, run, digest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("pending_references", await CodeAsync(refused));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(team.Approver, run, digest, acknowledge: true)).StatusCode);
    }

    [Fact]
    public async Task Large_import_needs_a_second_person()
    {
        var team = await TeamAsync();
        var both = host.AdminClient(await data.ActorAsync((team.Tenant, team.Slug), Permissions.ImportCreate, Permissions.ImportApprove));
        var csv = new StringBuilder("users\nid;registration;name\n");
        for (var i = 1; i <= 100; i++)
        {
            csv.Append(i).Append(";M").Append(i).Append(";Pessoa ").Append(i).Append('\n');
        }

        var file = await UploadAsync(both, team.Site, Zip("users.csv", csv.ToString()));
        await DrainAsync();
        var run = (await (await StartAsync(both, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();
        var digest = await SubmittedDigestAsync(both, run);

        var refused = await ApproveAsync(both, run, digest);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("second_approver_required", await CodeAsync(refused));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(team.Approver, run, digest)).StatusCode);
    }

    [Fact]
    public async Task Small_import_may_be_approved_by_whoever_prepared_it()
    {
        var team = await TeamAsync();
        var both = host.AdminClient(await data.ActorAsync((team.Tenant, team.Slug), Permissions.ImportCreate, Permissions.ImportApprove));
        var file = await UploadAsync(both, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        var run = (await (await StartAsync(both, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(both, run, await SubmittedDigestAsync(both, run))).StatusCode);
    }

    [Fact]
    public async Task Expired_preview_cannot_be_approved_nor_decided()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n2;MAT002;Bruno\n");
        await DecideAsync(team.Operator, run, new { lines = Line3, decision = ImportDecisions.AcceptSource });
        var digest = await SubmittedDigestAsync(team.Operator, run);
        await ExpireAsync(run);

        var refused = await ApproveAsync(team.Approver, run, digest);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("preview_expired", await CodeAsync(refused));
        Assert.Equal("expired", (await RunAsync(team.Operator, run)).GetProperty("status").GetString());
    }

    private async Task ExpireAsync(Guid run)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE hub.import_runs SET expires_at = now() - interval '1 minute' WHERE id = @r", connection);
        command.Parameters.AddWithValue("r", run);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rejection_closes_the_run_and_records_who()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        await SubmittedDigestAsync(team.Operator, run);

        var rejected = await team.Approver.PostAsJsonAsync(Url($"/api/v1/import-approvals/{run}/reject"), new { reason = "Conferir com o RH" });

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal("Conferir com o RH", body.GetProperty("closeReason").GetString());
    }

    // Permissoes e isolamento

    [Fact]
    public async Task Preparing_and_approving_are_separate_permissions()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        var digest = await SubmittedDigestAsync(team.Operator, run);

        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(team.Operator, run, digest)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Approver.GetAsync(Url($"/api/v1/import-runs/{run}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SubmitAsync(team.Approver, run)).StatusCode);
    }

    [Fact]
    public async Task Approver_without_second_factor_is_refused()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        var digest = await SubmittedDigestAsync(team.Operator, run);
        var actor = await data.ActorAsync((team.Tenant, team.Slug), Permissions.ImportApprove);
        using var noMfa = host.AdminClient(actor);
        noMfa.DefaultRequestHeaders.Remove("X-Test-Mfa");

        var response = await ApproveAsync(noMfa, run, digest);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("mfa_required", await CodeAsync(response));
    }

    [Fact]
    public async Task Approver_sees_pending_runs_and_rows_of_their_scope_only()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        await SubmittedDigestAsync(team.Operator, run);

        var pending = await (await team.Approver.GetAsync(Url("/api/v1/import-approvals"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(pending.EnumerateArray(), r => r.GetProperty("id").GetGuid() == run);
        Assert.Equal(HttpStatusCode.OK, (await team.Approver.GetAsync(Url($"/api/v1/import-approvals/{run}/rows"))).StatusCode);

        var otherSite = await data.SiteAsync(team.Tenant, "Outra");
        var scoped = await data.ActorAsync((team.Tenant, team.Slug));
        await data.GrantAsync(scoped, Permissions.ImportApprove, otherSite);
        using var limited = host.AdminClient(scoped);

        var theirs = await (await limited.GetAsync(Url("/api/v1/import-approvals"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(theirs.EnumerateArray(), r => r.GetProperty("id").GetGuid() == run);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/import-approvals/{run}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/import-approvals/{run}/rows"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(limited, run, "x")).StatusCode);
    }

    [Fact]
    public async Task Other_tenant_reaches_nothing()
    {
        var team = await TeamAsync();
        var other = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        var digest = await SubmittedDigestAsync(team.Operator, run);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Operator.GetAsync(Url($"/api/v1/import-runs/{run}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Operator.GetAsync(Url($"/api/v1/import-runs/{run}/rows"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Operator.GetAsync(Url($"/api/v1/import-runs/{run}/errors.csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(other.Operator, run, new { lines = Line1, decision = ImportDecisions.Skip })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(other.Operator, run)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Operator.PostAsJsonAsync(Url($"/api/v1/import-runs/{run}/cancel"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(other.Approver, run, digest)).StatusCode);
        Assert.Empty((await (await other.Operator.GetAsync(Url("/api/v1/import-runs"))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Empty((await (await other.Approver.GetAsync(Url("/api/v1/import-approvals"))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Other_tenant_cannot_start_a_run_or_read_columns()
    {
        var team = await TeamAsync();
        var other = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(other.Operator, file)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Operator.GetAsync(Url($"/api/v1/imports/{file}/columns"))).StatusCode);
        Assert.Empty((await (await other.Operator.GetAsync(Url($"/api/v1/imports/{file}/runs"))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Unknown_device_is_refused()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Operator, file, new { deviceId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Device_of_another_site_is_refused()
    {
        var team = await TeamAsync();
        var otherSite = await data.SiteAsync(team.Tenant, "Outra");
        var device = await DeviceAsync(team with { Site = otherSite });
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Operator, file, new { deviceId = device })).StatusCode);
    }

    [Fact]
    public async Task Operator_of_another_site_reaches_nothing_of_this_file()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        var file = (await RunAsync(team.Operator, run)).GetProperty("fileId").GetGuid();
        var otherSite = await data.SiteAsync(team.Tenant, "Outra");
        var scoped = await data.ActorAsync((team.Tenant, team.Slug));
        await data.GrantAsync(scoped, Permissions.ImportCreate, otherSite);
        using var limited = host.AdminClient(scoped);

        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(limited, file)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/imports/{file}/columns"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/import-runs/{run}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/import-runs/{run}/rows"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/import-runs/{run}/errors.csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(limited, run, new { lines = Line3, decision = ImportDecisions.Skip })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(limited, run)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.PostAsJsonAsync(Url($"/api/v1/import-runs/{run}/cancel"), new { })).StatusCode);
        Assert.Empty((await (await limited.GetAsync(Url("/api/v1/import-runs"))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Empty((await (await limited.GetAsync(Url($"/api/v1/imports/{file}/runs"))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    // Estados

    [Fact]
    public async Task File_still_in_inspection_cannot_start_a_preview()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));

        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(team.Operator, file)).StatusCode);
        await DrainAsync();
    }

    [Fact]
    public async Task Only_one_live_run_per_file_and_a_new_one_after_closing()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();

        var first = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(team.Operator, file)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await team.Operator.PostAsJsonAsync(Url($"/api/v1/import-runs/{first}/cancel"), new { reason = "Refazer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await StartAsync(team.Operator, file)).StatusCode);

        var history = await (await team.Operator.GetAsync(Url($"/api/v1/imports/{file}/runs"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, history.GetArrayLength());
        await DrainAsync();
    }

    [Fact]
    public async Task Expired_run_does_not_block_a_new_attempt()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        var first = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();
        await ExpireAsync(first);

        Assert.Equal(HttpStatusCode.Created, (await StartAsync(team.Operator, file)).StatusCode);
        await DrainAsync();
    }

    [Fact]
    public async Task Run_cancelled_during_processing_leaves_no_rows()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        var run = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await team.Operator.PostAsJsonAsync(Url($"/api/v1/import-runs/{run}/cancel"), new { })).StatusCode);
        await DrainAsync();

        Assert.Equal("cancelled", (await RunAsync(team.Operator, run)).GetProperty("status").GetString());
        Assert.Empty(await RowsAsync(team.Operator, run));
    }

    /// <summary>Cancela no meio do processamento: a execucao ja foi pega pelo worker e esta lendo o original.</summary>
    public class S3WithHook : System.Reflection.DispatchProxy
    {
        public Amazon.S3.IAmazonS3 Inner { get; set; } = null!;

        public Func<Task>? BeforeFirstRead { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name.StartsWith("GetObject", StringComparison.Ordinal) && BeforeFirstRead is { } hook)
            {
                BeforeFirstRead = null;
                hook().GetAwaiter().GetResult();
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    [Fact]
    public async Task Run_cancelled_while_being_processed_leaves_no_rows()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        var run = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var proxy = System.Reflection.DispatchProxy.Create<Amazon.S3.IAmazonS3, S3WithHook>();
        var hooked = (S3WithHook)(object)proxy;
        hooked.Inner = s3;
        hooked.BeforeFirstRead = async () => Assert.Equal(HttpStatusCode.OK, (await team.Operator.PostAsJsonAsync(Url($"/api/v1/import-runs/{run}/cancel"), new { })).StatusCode);
        var store = new EncryptedBlobStore(new S3ObjectStore(proxy, StorageFixture.Bucket), new TransitKeyService(kms), new TenantDatabase(workerSource));
        var previews = new ImportPreviewService(workerSource, new TenantDatabase(workerSource), store, new CredentialProtector(new TransitKeyService(kms)), new PreviewLimits(), NullLogger<ImportPreviewService>.Instance);

        Assert.True(await previews.ProcessNextAsync(None));

        Assert.Null(hooked.BeforeFirstRead);
        Assert.Equal("cancelled", (await RunAsync(team.Operator, run)).GetProperty("status").GetString());
        Assert.Empty(await RowsAsync(team.Operator, run));
    }

    [Fact]
    public async Task Interrupted_processing_restarts_from_scratch_without_duplicating_rows()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var reset = new NpgsqlCommand("UPDATE hub.import_runs SET status = 'processing', started_at = now() - interval '1 hour' WHERE id = @r; DELETE FROM hub.import_rows WHERE run_id = @r;", connection);
            reset.Parameters.AddWithValue("r", run);
            await reset.ExecuteNonQueryAsync();
        }

        await DrainAsync();

        Assert.Equal("preview_ready", (await RunAsync(team.Operator, run)).GetProperty("status").GetString());
        Assert.Equal(2, (await RowsAsync(team.Operator, run)).Count);
    }

    [Fact]
    public async Task Run_with_too_many_attempts_fails_instead_of_looping()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        var run = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var bump = new NpgsqlCommand("UPDATE hub.import_runs SET attempts = 3 WHERE id = @r", connection);
            bump.Parameters.AddWithValue("r", run);
            await bump.ExecuteNonQueryAsync();
        }

        await DrainAsync();

        var body = await RunAsync(team.Operator, run);
        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal(RunErrors.PreviewFailed, body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Destroyed_original_fails_the_run_with_a_clear_reason()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT encrypted_blob_ref FROM hub.import_files WHERE id = @f", connection);
            query.Parameters.AddWithValue("f", file);
            await Store().DestroyAsync(team.Tenant, Guid.Parse((string)(await query.ExecuteScalarAsync())!), None);
        }

        var run = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();

        var body = await RunAsync(team.Operator, run);
        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal(RunErrors.OriginalUnavailable, body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Too_many_people_fails_with_a_clear_reason()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();
        var run = (await (await StartAsync(team.Operator, file)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await DrainAsync(new PreviewLimits { MaxPeople = 1 });

        Assert.Equal(RunErrors.TooManyPeople, (await RunAsync(team.Operator, run)).GetProperty("errorCode").GetString());
    }

    // Outros objetos

    [Fact]
    public async Task Biometrics_and_credentials_are_only_counted_and_never_stored()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(
            team,
            "users\nid;registration;name;password;salt\n1;MAT001;Ana Souza;MARCADORHASH;MARCADORSALT\ncards\nid;value;user_id\n5;18446744073709551615;1\ntemplates\nid;finger_type;template;user_id\n7;0;TUFSQ0FET1JCSU8=;1\npins\nid;value;user_id\n8;MARCADORPIN;1\n");

        var body = await RunAsync(team.Operator, run);
        var stats = body.GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("biometricsExcluded").GetInt64());
        Assert.Equal(1, stats.GetProperty("credentials").GetProperty("cards").GetProperty("new").GetInt64());
        Assert.Equal(1, stats.GetProperty("credentialsNotCompared").GetProperty("pins").GetInt64());

        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        foreach (var table in new[] { "import_runs", "import_rows", "import_files", "audit_log" })
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM hub.{table} t WHERE t.tenant_id = @t AND (t::text LIKE '%MARCADOR%' OR t::text LIKE '%Souza%' OR t::text LIKE '%MAT001%' OR t::text LIKE '%18446744073709551615%' OR t::text LIKE '%TUFSQ0FET1JCSU8%')",
                connection);
            command.Parameters.AddWithValue("t", team.Tenant);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Logs_without_a_device_are_not_compared_and_are_not_called_new()
    {
        var team = await TeamAsync();
        var preview = await RunAsync(team.Operator, await PreviewAsync(team, "id;time;event\n1;1700000000;7\n2;1700000001;7\n", name: "logs.csv"));

        var events = preview.GetProperty("stats").GetProperty("events");
        Assert.Equal(2, events.GetProperty("total").GetInt64());
        Assert.Equal(2, events.GetProperty("notCompared").GetInt64());
        Assert.Equal(0, events.GetProperty("new").GetInt64());
    }

    [Fact]
    public async Task Event_already_received_from_the_device_is_recognized()
    {
        var team = await TeamAsync();
        var device = await DeviceAsync(team);
        await EventAsync(team.Tenant, device, "1");
        var file = await UploadAsync(team.Operator, team.Site, Zip("logs.csv", "id;time;event\n1;1700000000;7\n2;1700000001;7\n"));
        await DrainAsync();

        var run = (await (await StartAsync(team.Operator, file, new { deviceId = device })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();

        var events = (await RunAsync(team.Operator, run)).GetProperty("stats").GetProperty("events");
        Assert.Equal(1, events.GetProperty("alreadyImported").GetInt64());
        Assert.Equal(1, events.GetProperty("new").GetInt64());
        Assert.Equal(0, events.GetProperty("notCompared").GetInt64());
    }

    [Fact]
    public async Task Event_of_a_previous_epoch_of_the_device_is_not_the_same_event()
    {
        // Depois de reset de fabrica os IDs recomecam: o mesmo numero e outro evento.
        var team = await TeamAsync();
        var device = await DeviceAsync(team);
        await EventAsync(team.Tenant, device, "1");
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var bump = new NpgsqlCommand("UPDATE hub.devices SET source_epoch = source_epoch + 1 WHERE id = @d", connection);
            bump.Parameters.AddWithValue("d", device);
            await bump.ExecuteNonQueryAsync();
        }

        var file = await UploadAsync(team.Operator, team.Site, Zip("logs.csv", "id;time;event\n1;1700000000;7\n"));
        await DrainAsync();
        var run = (await (await StartAsync(team.Operator, file, new { deviceId = device })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();

        var events = (await RunAsync(team.Operator, run)).GetProperty("stats").GetProperty("events");
        Assert.Equal(0, events.GetProperty("alreadyImported").GetInt64());
        Assert.Equal(1, events.GetProperty("new").GetInt64());
    }

    [Fact]
    public async Task Link_with_the_device_is_the_first_match()
    {
        var team = await TeamAsync();
        var device = await DeviceAsync(team);
        var person = await PersonAsync(team.Tenant, "Ana Souza", "MATERIA-ANTIGA", team.Site);
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var link = new NpgsqlCommand("INSERT INTO hub.person_device_links (tenant_id, person_id, device_id, source_user_id) VALUES (@t, @p, @d, 1)", connection);
            link.Parameters.AddWithValue("t", team.Tenant);
            link.Parameters.AddWithValue("p", person);
            link.Parameters.AddWithValue("d", device);
            await link.ExecuteNonQueryAsync();
        }

        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", "users\nid;registration;name\n1;MAT-NOVA;Ana Souza\n"));
        await DrainAsync();
        var run = (await (await StartAsync(team.Operator, file, new { deviceId = device })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();

        var row = (await RowsAsync(team.Operator, run)).Single();
        Assert.Equal(PreviewKinds.FieldConflict, row.GetProperty("kind").GetString());
        Assert.Equal(person, row.GetProperty("matchPersonId").GetGuid());
    }

    private async Task<Guid> DeviceAsync(Team team)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var gateway = new NpgsqlCommand("INSERT INTO hub.edge_gateways (tenant_id, site_id, name) VALUES (@t, @s, 'g') RETURNING id", connection);
        gateway.Parameters.AddWithValue("t", team.Tenant);
        gateway.Parameters.AddWithValue("s", team.Site);
        var gatewayId = (Guid)(await gateway.ExecuteScalarAsync())!;
        await using var device = new NpgsqlCommand("INSERT INTO hub.devices (tenant_id, site_id, gateway_id, serial_number, name) VALUES (@t, @s, @g, @n, 'd') RETURNING id", connection);
        device.Parameters.AddWithValue("t", team.Tenant);
        device.Parameters.AddWithValue("s", team.Site);
        device.Parameters.AddWithValue("g", gatewayId);
        device.Parameters.AddWithValue("n", Guid.NewGuid().ToString());
        return (Guid)(await device.ExecuteScalarAsync())!;
    }

    private async Task EventAsync(Guid tenant, Guid device, string sourceId)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO hub.access_events (tenant_id, device_id, source_family, source_event_id, event_kind, ingress_source, dedup_hash) VALUES (@t, @d, 'access_logs', @s, 'granted', 'monitor', @h)",
            connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("d", device);
        command.Parameters.AddWithValue("s", sourceId);
        command.Parameters.AddWithValue("h", RandomNumberGenerator.GetBytes(32));
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rows_are_paginated_and_filtered()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers + "x;MAT003;Invalido\n");

        Assert.Single(await RowsAsync(team.Operator, run, PreviewKinds.Invalid));

        var first = await (await team.Operator.GetAsync(Url($"/api/v1/import-runs/{run}/rows?limit=2"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetInt64();
        var second = await (await team.Operator.GetAsync(Url($"/api/v1/import-runs/{run}/rows?limit=2&cursor={cursor}"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Error_report_lists_reasons_without_any_cell_value()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, "users\nid;registration;name\n=HYPERLINK(1);MARCADORMAT;MARCADORNOME\n2;MAT002;Bruno\n");

        var response = await team.Operator.GetAsync(Url($"/api/v1/import-runs/{run}/errors.csv"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("3;users;invalid_integer", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("MARCADOR", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("HYPERLINK", csv, StringComparison.Ordinal);
    }

    // Credenciais comparadas com o cadastro

    private async Task CredentialAsync(Guid tenant, Guid person, string kind, string value)
    {
        var index = await new CredentialProtector(new TransitKeyService(kms)).IndexAsync(tenant, kind, value, None);
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO hub.credentials (tenant_id, person_id, kind, ciphertext, blind_index) VALUES (@t, @p, @k, '\\x00', @i)", connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("p", person);
        command.Parameters.AddWithValue("k", kind);
        command.Parameters.AddWithValue("i", index);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Card_already_of_the_same_person_is_recognized_and_new_card_is_counted()
    {
        var team = await TeamAsync();
        var person = await PersonAsync(team.Tenant, "Ana Souza", "MAT001", team.Site);
        await CredentialAsync(team.Tenant, person, "card", "42");

        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\ncards\nid;value;user_id\n5;042;1\n6;43;1\n");

        var body = await RunAsync(team.Operator, run);
        var cards = body.GetProperty("stats").GetProperty("credentials").GetProperty("cards");
        Assert.Equal(1, cards.GetProperty("samePerson").GetInt64());
        Assert.Equal(1, cards.GetProperty("new").GetInt64());
        Assert.Equal(0, cards.GetProperty("otherPerson").GetInt64());
        Assert.Equal(0, body.GetProperty("unresolvedDecisions").GetInt64());
    }

    [Theory]
    [InlineData("MAT002")]
    [InlineData("NOVA")]
    public async Task Card_of_another_person_needs_a_decision_and_cannot_be_taken_from_them(string registration)
    {
        var team = await TeamAsync();
        var owner = await PersonAsync(team.Tenant, "Dona Do Cartao", "MAT001", team.Site);
        await CredentialAsync(team.Tenant, owner, "card", "42");
        await PersonAsync(team.Tenant, "Outra Pessoa", "MAT002", team.Site);

        var run = await PreviewAsync(team, $"users\nid;registration;name\n1;{registration};Outra Pessoa\ncards\nid;value;user_id\n5;42;1\n");

        var conflict = (await RowsAsync(team.Operator, run, PreviewKinds.FieldConflict)).Single(r => r.GetProperty("sourceObject").GetString() == "cards");
        Assert.Contains(PreviewCodes.CredentialOfOtherPerson, conflict.GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
        Assert.DoesNotContain(owner.ToString(), conflict.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dona", conflict.GetRawText(), StringComparison.Ordinal);
        Assert.True((await RunAsync(team.Operator, run)).GetProperty("unresolvedDecisions").GetInt64() >= 1);

        var line = conflict.GetProperty("lineNo").GetInt64();
        var refused = await DecideAsync(team.Operator, run, new { lines = LineOf(line), decision = ImportDecisions.AcceptSource });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(team.Operator, run, new { lines = LineOf(line), decision = ImportDecisions.Skip })).StatusCode);
    }

    [Fact]
    public async Task Bulk_accept_does_not_take_a_card_from_another_person()
    {
        var team = await TeamAsync();
        var owner = await PersonAsync(team.Tenant, "Dona Do Cartao", "MAT001", team.Site);
        await CredentialAsync(team.Tenant, owner, "card", "42");

        var run = await PreviewAsync(team, "users\nid;registration;name\n1;NOVA;Outra Pessoa\ncards\nid;value;user_id\n5;42;1\n");
        await DecideAsync(team.Operator, run, new { kind = PreviewKinds.FieldConflict, decision = ImportDecisions.AcceptSource });

        Assert.Equal(1, (await RunAsync(team.Operator, run)).GetProperty("unresolvedDecisions").GetInt64());
    }

    [Fact]
    public async Task Revoked_card_is_new_again()
    {
        var team = await TeamAsync();
        var owner = await PersonAsync(team.Tenant, "Ex Dona", "MAT001", team.Site);
        await CredentialAsync(team.Tenant, owner, "card", "42");
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var revoke = new NpgsqlCommand("UPDATE hub.credentials SET revoked_at = now() WHERE person_id = @p", connection);
            revoke.Parameters.AddWithValue("p", owner);
            await revoke.ExecuteNonQueryAsync();
        }

        var run = await PreviewAsync(team, "users\nid;registration;name\n1;NOVA;Outra Pessoa\ncards\nid;value;user_id\n5;42;1\n");

        Assert.Equal(1, (await RunAsync(team.Operator, run)).GetProperty("stats").GetProperty("credentials").GetProperty("cards").GetProperty("new").GetInt64());
    }

    [Fact]
    public async Task Pin_is_not_compared_and_stays_out_of_every_table()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\npins\nid;value;user_id\n8;908172;1\n");

        Assert.Equal(1, (await RunAsync(team.Operator, run)).GetProperty("stats").GetProperty("credentialsNotCompared").GetProperty("pins").GetInt64());
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM hub.import_rows t WHERE t.tenant_id = @t AND t::text LIKE '%908172%'", connection);
        command.Parameters.AddWithValue("t", team.Tenant);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task External_key_of_the_declared_system_is_a_match()
    {
        var team = await TeamAsync();
        var person = await PersonAsync(team.Tenant, "Ana Souza", "MATRICULA-ANTIGA", team.Site);
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var key = new NpgsqlCommand("INSERT INTO hub.entity_source_keys (tenant_id, entity_type, source_system, external_key, entity_id) VALUES (@t, 'person', 'rh', 'E-77', @p)", connection);
            key.Parameters.AddWithValue("t", team.Tenant);
            key.Parameters.AddWithValue("p", person);
            await key.ExecuteNonQueryAsync();
        }

        var file = await SpreadsheetAsync(team, "chave;nome;matricula\nE-77;Ana Souza;MAT-NOVA\n");
        var mapping = new
        {
            delimiter = ";",
            hasHeader = true,
            columns = new object[] { new { index = 0, target = "source_id" }, new { index = 1, target = PersonFields.DisplayName }, new { index = 2, target = PersonFields.Registration } },
        };

        var withSystem = (await (await StartAsync(team.Operator, file, new { mapping, sourceSystem = "rh" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();
        var matched = (await RowsAsync(team.Operator, withSystem)).Single();
        Assert.Equal(person, matched.GetProperty("matchPersonId").GetGuid());
        await team.Operator.PostAsJsonAsync(Url($"/api/v1/import-runs/{withSystem}/cancel"), new { });

        // Outro sistema nao e a mesma chave: so sobra a suspeita pelo nome.
        var otherSystem = (await (await StartAsync(team.Operator, file, new { mapping, sourceSystem = "ponto" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();
        var notMatched = (await RowsAsync(team.Operator, otherSystem)).Single();
        Assert.Equal(JsonValueKind.Null, notMatched.GetProperty("matchPersonId").ValueKind);
        Assert.Equal(PreviewKinds.PossibleDuplicate, notMatched.GetProperty("kind").GetString());
    }

    // Planilha

    private static object Mapping(string delimiter = ";") => new
    {
        mapping = new
        {
            delimiter,
            hasHeader = true,
            columns = new object[]
            {
                new { index = 0, target = PersonFields.DisplayName },
                new { index = 1, target = PersonFields.Registration },
            },
        },
    };

    private async Task<Guid> SpreadsheetAsync(Team team, string csv = "nome;matricula\nAna Souza;00123\n")
    {
        var file = await UploadAsync(team.Operator, team.Site, Encoding.UTF8.GetBytes(csv));
        await DrainAsync();
        return file;
    }

    [Fact]
    public async Task Spreadsheet_requires_a_mapping_made_by_a_person()
    {
        var team = await TeamAsync();
        var file = await SpreadsheetAsync(team);

        var refused = await StartAsync(team.Operator, file);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("mapping_required", await CodeAsync(refused));

        var started = await StartAsync(team.Operator, file, Mapping());
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DrainAsync();

        var row = (await RowsAsync(team.Operator, run)).Single();
        Assert.Equal(PreviewKinds.New, row.GetProperty("kind").GetString());
        Assert.Equal(2, row.GetProperty("lineNo").GetInt64());
    }

    [Fact]
    public async Task Columns_show_names_and_masked_samples_and_are_not_persisted()
    {
        var team = await TeamAsync();
        var file = await SpreadsheetAsync(team, "nome;matricula\nMARCADORNOME Silva;MARCADOR123456\n");

        var response = await team.Operator.GetAsync(Url($"/api/v1/imports/{file}/columns"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(";", body.GetProperty("delimiter").GetString());
        Assert.Equal("nome", body.GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("MARCADOR", text, StringComparison.Ordinal);

        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM hub.audit_log WHERE tenant_id = @t AND action = 'import.columns_read'", connection);
        command.Parameters.AddWithValue("t", team.Tenant);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Without_header_columns_have_no_name_because_the_first_row_is_data()
    {
        var team = await TeamAsync();
        var file = await SpreadsheetAsync(team, "MARCADORNOME;123\nOutra Pessoa;456\n");

        var text = await (await team.Operator.GetAsync(Url($"/api/v1/imports/{file}/columns?hasHeader=false"))).Content.ReadAsStringAsync();

        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("columns")[0].GetProperty("name").ValueKind);
        Assert.DoesNotContain("MARCADOR", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_delimiter_asks_a_person_to_choose()
    {
        var team = await TeamAsync();
        var file = await SpreadsheetAsync(team, "a,b;c,d\n1,2;3,4\n");

        var response = await team.Operator.GetAsync(Url($"/api/v1/imports/{file}/columns"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("delimiter_required", await CodeAsync(response));

        Assert.Equal(HttpStatusCode.OK, (await team.Operator.GetAsync(Url($"/api/v1/imports/{file}/columns?delimiter=%3B"))).StatusCode);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("pin")]
    public async Task Mapping_to_a_field_outside_the_registry_is_refused(string target)
    {
        var team = await TeamAsync();
        var file = await SpreadsheetAsync(team);
        var body = new
        {
            mapping = new
            {
                delimiter = ";",
                hasHeader = true,
                columns = new object[] { new { index = 0, target = PersonFields.DisplayName }, new { index = 1, target = PersonFields.Registration }, new { index = 2, target } },
            },
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Operator, file, body)).StatusCode);
    }

    [Fact]
    public async Task Mapping_is_stored_without_the_header_text()
    {
        var team = await TeamAsync();
        var file = await SpreadsheetAsync(team, "MARCADORCOLUNA;matricula\nAna;123\n");

        Assert.Equal(HttpStatusCode.Created, (await StartAsync(team.Operator, file, Mapping())).StatusCode);
        await DrainAsync();

        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM hub.import_runs t WHERE t.tenant_id = @t AND t::text LIKE '%MARCADOR%'", connection);
        command.Parameters.AddWithValue("t", team.Tenant);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Unreadable_file_cannot_start_a_preview_even_with_a_mapping()
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", "users\nid;name\n1;\"sem fechar\n"));
        await DrainAsync();

        var response = await StartAsync(team.Operator, file, Mapping());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("file_unreadable", await CodeAsync(response));
    }

    [Theory]
    [InlineData("display_name", "acima")]
    [InlineData("password", "manual")]
    public async Task Invalid_field_policy_is_refused(string field, string policy)
    {
        var team = await TeamAsync();
        var file = await UploadAsync(team.Operator, team.Site, Zip("users.csv", TwoUsers));
        await DrainAsync();

        var body = new { fieldPolicy = new Dictionary<string, string> { [field] = policy } };
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Operator, file, body)).StatusCode);
    }

    [Fact]
    public async Task Field_policy_chosen_by_the_operator_is_applied()
    {
        var team = await TeamAsync();
        await PersonAsync(team.Tenant, "Nome Antigo", "MAT001", team.Site);

        var run = await PreviewAsync(team, "users\nid;registration;name\n1;MAT001;Ana Souza\n", new { fieldPolicy = new Dictionary<string, string> { [PersonFields.DisplayName] = FieldPolicies.SourceWins } });

        Assert.Equal(1, People(await RunAsync(team.Operator, run), PreviewKinds.Update));
    }

    // Auditoria

    [Fact]
    public async Task Each_step_is_audited()
    {
        var team = await TeamAsync();
        var run = await PreviewAsync(team, TwoUsers);
        await ApproveAsync(team.Approver, run, await SubmittedDigestAsync(team.Operator, run));

        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT array_agg(action) FROM hub.audit_log WHERE tenant_id = @t AND resource_id = @r", connection);
        command.Parameters.AddWithValue("t", team.Tenant);
        command.Parameters.AddWithValue("r", run.ToString());
        var actions = (string[])(await command.ExecuteScalarAsync())!;

        Assert.Contains("import.run.create", actions);
        Assert.Contains("import.preview_ready", actions);
        Assert.Contains("import.submit", actions);
        Assert.Contains("import.approve", actions);
    }
}
