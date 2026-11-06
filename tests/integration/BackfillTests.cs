using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.ContractTests;
using Platform.EdgeGateway;
using Platform.Modules.Fleet;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Authorization;

namespace Platform.IntegrationTests;

/// <summary>Backfill de ponta a ponta com equipamento SIMULADO. O formato vem da doc; reset real nunca foi observado.</summary>
[Collection(PostgresTests.Name)]
public sealed class BackfillTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Grants =
    [
        Permissions.GatewaysRead, Permissions.GatewaysManage, Permissions.DeviceRead, Permissions.DeviceWrite,
        Permissions.DeviceProbe, Permissions.DeviceSettingsWrite, Permissions.DeviceBackfill, Permissions.EventsRead,
    ];

    private readonly TestData data;
    private readonly EdgeHost host;
    private readonly X509Certificate2 deviceCertificate = TestCertificates.SelfSigned();
    private readonly string identityPath = Path.Combine(Path.GetTempPath(), "hub-gw-" + Guid.NewGuid().ToString("N") + ".pfx");
    private readonly string spoolPath = Path.Combine(Path.GetTempPath(), "hub-spool-" + Guid.NewGuid().ToString("N"));
    private ControlIdSimulator simulator = null!;
    private HttpClient admin = null!;
    private CentralClient central = null!;
    private X509Certificate2 identity = null!;
    private GatewayAgent agent = null!;
    private Guid device;
    private Guid tenant;

    public BackfillTests(PostgresFixture postgres, StorageFixture storage)
    {
        data = new TestData(postgres.AdminOnHubConnection);
        host = new EdgeHost(postgres, storage);
    }

    private static CancellationToken None => CancellationToken.None;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    public async Task InitializeAsync()
    {
        simulator = await ControlIdSimulator.StartAsync(deviceCertificate);
        var owner = await data.TenantAsync();
        tenant = owner.Id;
        var site = await data.SiteAsync(tenant);
        admin = host.AdminClient(await data.ActorAsync(owner, Grants));

        var gateway = (await (await admin.PostAsJsonAsync(Url("/api/v1/gateways"), new { siteId = site, name = "Portaria" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var token = (await (await admin.PostAsync(Url($"/api/v1/gateways/{gateway}/enrollment-token"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var created = await admin.PostAsJsonAsync(Url("/api/v1/devices"), new { siteId = site, gatewayId = gateway, name = "Catraca", host = "127.0.0.1", port = simulator.Address.Port, tlsPin = TestCertificates.Pin(deviceCertificate) });
        device = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync(Url($"/api/v1/devices/{device}/credential"), new { login = ControlIdSimulator.ValidLogin, password = ControlIdSimulator.ValidPassword });
        await data.ExecuteAsync("UPDATE hub.devices SET source_device_id = 478435 WHERE id = @device", new { device });

        identity = GatewayIdentity.LoadOrCreate(identityPath);
        central = host.Central(identity);
        await central.EnrollAsync(new EnrollRequest(token, null), None);
        agent = BuildAgent(central);
    }

    public async Task DisposeAsync()
    {
        admin.Dispose();
        central.Dispose();
        identity.Dispose();
        await simulator.DisposeAsync();
        deviceCertificate.Dispose();
        File.Delete(identityPath);
        if (Directory.Exists(spoolPath))
        {
            Directory.Delete(spoolPath, recursive: true);
        }
    }

    public void Dispose() => host.Dispose();

    private static GatewayAgent BuildAgent(CentralClient client, EventSpool? spool = null)
    {
        var options = new GatewayOptions { ConnectorEnabled = true, AllowedCidrs = ["127.0.0.0/8"] };
        return new GatewayAgent(client, new JobExecutor(options, NullLoggerFactory.Instance, null, client), options, NullLogger<GatewayAgent>.Instance, null, spool);
    }

    private static string Log(int id, int time = 1700000000, int code = 7) =>
        $$"""{"id":{{id}},"time":{{time + id}},"event":{{code}},"user_id":3,"portal_id":1,"pin_value":"PINSECRETO","card_value":99,"qrcode_value":"QRSECRETO"}""";

    private void SeedLogs(params int[] ids) =>
        simulator.Tables["access_logs"] = ids.Select(i => Log(i)).ToList();

    private async Task<Guid> RequestBackfillAsync()
    {
        var response = await admin.PostAsync(Url($"/api/v1/devices/{device}/backfill"), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> RunBackfillAsync()
    {
        var job = await RequestBackfillAsync();
        await agent.RunCycleAsync(None);
        return await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<long> StoredAsync(int? epoch = null) => data.ScalarAsync<long>(
        "SELECT count(*) FROM hub.access_events WHERE device_id = @device AND source_family = 'access_logs' AND (@epoch::int IS NULL OR source_epoch = @epoch)",
        new { device, epoch });

    private async Task<JsonElement> StateAsync() =>
        await (await admin.GetAsync(Url($"/api/v1/devices/{device}/sync-state"))).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Recovers_every_log_lost_during_an_outage_and_saves_the_cursor()
    {
        SeedLogs(1, 2, 3, 4, 5);

        var job = await RunBackfillAsync();

        Assert.Equal("completed", job.GetProperty("status").GetString());
        Assert.Equal(5, await StoredAsync());
        var cursor = (await StateAsync()).GetProperty("cursors").EnumerateArray().Single(c => c.GetProperty("objectName").GetString() == "access_logs");
        Assert.Equal("5", cursor.GetProperty("lastId").GetString());
        var stored = await data.ScalarAsync<string>("SELECT ingress_source FROM hub.access_events WHERE device_id = @device LIMIT 1", new { device });
        Assert.Equal("api", stored);
    }

    [Fact]
    public async Task Event_seen_by_monitor_and_by_backfill_is_stored_once()
    {
        SeedLogs(519);
        var spool = new EventSpool(spoolPath);
        var fromMonitor = MonitorNormalizer.Normalize("dao", Encoding.UTF8.GetBytes(MonitorSamples.Dao), device, DateTimeOffset.UtcNow)!;
        await spool.TryAppendAsync(fromMonitor[0], None);
        await BuildAgent(central, spool).DrainEventsAsync(None);
        Assert.Equal(1, await StoredAsync());

        await RunBackfillAsync();

        Assert.Equal(1, await StoredAsync());
        Assert.Equal("monitor", await data.ScalarAsync<string>("SELECT ingress_source FROM hub.access_events WHERE device_id = @device", new { device }));
    }

    [Fact]
    public async Task Second_run_reads_only_what_is_new()
    {
        SeedLogs(1, 2, 3);
        await RunBackfillAsync();
        simulator.Tables["access_logs"].Add(Log(4));
        while (simulator.Requests.TryDequeue(out _))
        {
        }

        await RunBackfillAsync();

        Assert.Equal(4, await StoredAsync());
        var reads = simulator.Requests.Where(r => r.Path == "/load_objects.fcgi" && r.Body.Contains("\"object\":\"access_logs\"", StringComparison.Ordinal)).ToList();
        Assert.All(reads, r => Assert.Contains("\"where\"", r.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reading_ends_on_an_empty_page_so_a_short_page_does_not_stop_it_early()
    {
        SeedLogs(1, 2, 3, 4, 5);
        var real = simulator.Tables["access_logs"];
        simulator.Overrides["/load_objects.fcgi"] = async c =>
        {
            // Firmware com limite proprio: devolve no maximo 2 linhas, mesmo pedindo 200.
            using var reader = new StreamReader(c.Request.Body);
            c.Response.ContentType = "application/json";
            var last = simulator.Requests.Last(r => r.Path == "/load_objects.fcgi").Body;
            using var json = JsonDocument.Parse(last);
            var name = json.RootElement.GetProperty("object").GetString()!;
            var minimum = json.RootElement.TryGetProperty("where", out var where) ? where.GetProperty(name).GetProperty("id").GetProperty(">").GetInt64() : 0;
            var rows = name == "access_logs" ? real.Select(r => JsonDocument.Parse(r).RootElement).Where(r => r.GetProperty("id").GetInt64() > minimum).Take(2).Select(r => $$"""{"id":{{r.GetProperty("id").GetInt64()}},"time":{{r.GetProperty("time").GetInt64()}},"event":7}""") : [];
            await c.Response.WriteAsync($"{{\"{name}\":[{string.Join(',', rows)}]}}");
        };

        await RunBackfillAsync();

        Assert.Equal(5, await StoredAsync());
    }

    [Fact]
    public async Task Secrets_from_the_device_never_reach_the_database()
    {
        simulator.IgnoreFields = true;
        SeedLogs(1, 2);

        await RunBackfillAsync();

        Assert.Equal(2, await StoredAsync());
        foreach (var secret in new[] { "PINSECRETO", "QRSECRETO" })
        {
            Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.access_events t WHERE t::text LIKE '%' || @secret || '%'", new { secret }));
        }

        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.access_events WHERE device_id = @device AND details ? 'card_value'", new { device }));
    }

    [Fact]
    public async Task Factory_reset_with_lower_ids_opens_a_new_epoch_without_losing_new_events()
    {
        SeedLogs(1, 2, 3, 4, 5);
        await RunBackfillAsync();

        // Reset: tudo apagado, e os IDs recomecam abaixo do cursor.
        simulator.Tables["access_logs"] = [Log(1, time: 1800000000), Log(2, time: 1800000000)];
        var job = await RunBackfillAsync();

        Assert.Equal("completed", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("result").GetProperty("epochReset").GetBoolean());
        Assert.Equal(1, (await StateAsync()).GetProperty("sourceEpoch").GetInt32());
        Assert.Equal(5, await StoredAsync(0));
        Assert.Equal(2, await StoredAsync(1));
        Assert.Equal(1L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.audit_log WHERE tenant_id = @tenant AND action = 'device.epoch_reset'", new { tenant }));
    }

    [Fact]
    public async Task Reset_that_already_passed_the_cursor_is_recognized_by_the_row_time()
    {
        SeedLogs(1, 2, 3);
        await RunBackfillAsync();

        // Mais linhas que antes: comparar so o maior id nao perceberia.
        simulator.Tables["access_logs"] = Enumerable.Range(1, 6).Select(i => Log(i, time: 1800000000)).ToList();
        await RunBackfillAsync();

        Assert.Equal(1, (await StateAsync()).GetProperty("sourceEpoch").GetInt32());
        Assert.Equal(6, await StoredAsync(1));
    }

    [Fact]
    public async Task Rotation_that_removes_old_rows_is_not_a_reset()
    {
        SeedLogs(1, 2, 3, 4, 5);
        await RunBackfillAsync();

        simulator.Tables["access_logs"] = [Log(7), Log(8)];
        await RunBackfillAsync();

        Assert.Equal(0, (await StateAsync()).GetProperty("sourceEpoch").GetInt32());
        Assert.Equal(7, await StoredAsync());
    }

    [Fact]
    public async Task Firmware_ignoring_the_filter_stops_without_a_false_reset_or_moved_cursor()
    {
        SeedLogs(1, 2, 3);
        await RunBackfillAsync();
        simulator.UnderstoodWhere = "list";

        var job = await RunBackfillAsync();

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Equal("UnexpectedBehavior", job.GetProperty("failureCode").GetString());
        var state = await StateAsync();
        Assert.Equal(0, state.GetProperty("sourceEpoch").GetInt32());
        Assert.Equal("3", state.GetProperty("cursors").EnumerateArray().Single(c => c.GetProperty("objectName").GetString() == "access_logs").GetProperty("lastId").GetString());
    }

    [Fact]
    public async Task Object_missing_from_a_firmware_does_not_block_the_others()
    {
        // Firmware sem alarm_logs: responde erro so para esse objeto.
        simulator.Overrides["/load_objects.fcgi"] = async c =>
        {
            var body = simulator.Requests.Last(r => r.Path == "/load_objects.fcgi").Body;
            c.Response.ContentType = "application/json";
            if (body.Contains("\"object\":\"alarm_logs\"", StringComparison.Ordinal))
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await c.Response.WriteAsync(body.Contains("\"object\":\"access_logs\"", StringComparison.Ordinal) && !body.Contains("where", StringComparison.Ordinal)
                ? "{\"access_logs\":[{\"id\":1,\"time\":1700000001,\"event\":7}]}"
                : body.Contains("\"object\":\"access_logs\"", StringComparison.Ordinal) ? "{\"access_logs\":[]}" : "{\"access_events\":[]}");
        };

        var job = await RunBackfillAsync();

        Assert.Equal("completed", job.GetProperty("status").GetString());
        Assert.Equal(1, await StoredAsync());
        var reports = job.GetProperty("result").GetProperty("objects").EnumerateArray().ToList();
        Assert.Equal("DeviceError", reports.Single(r => r.GetProperty("objectName").GetString() == "alarm_logs").GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, reports.Single(r => r.GetProperty("objectName").GetString() == "access_logs").GetProperty("failure").ValueKind);
    }

    [Fact]
    public async Task Failure_of_every_object_is_reported_as_failed_not_completed()
    {
        simulator.Overrides["/load_objects.fcgi"] = c =>
        {
            c.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        };

        var job = await RunBackfillAsync();

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Equal("all_objects_failed", job.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task Access_events_are_recovered_as_their_own_family()
    {
        simulator.Tables["access_events"] =
        [
            """{"id":1,"event":"door","type":"OPEN","identification":"1","device_id":478435,"timestamp":1700000001}""",
            """{"id":2,"event":"catra","type":"TURN_LEFT","identification":"1","device_id":478435,"timestamp":1700000002}""",
        ];

        await RunBackfillAsync();

        var page = await (await admin.GetAsync(Url("/api/v1/events?eventKind=door_opened"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("access_events", page.GetProperty("items")[0].GetProperty("sourceFamily").GetString());
        var turn = await (await admin.GetAsync(Url("/api/v1/events?eventKind=turnstile_turn_left"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, turn.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Stale_lease_cannot_move_the_cursor()
    {
        SeedLogs(1);
        var job = await RequestBackfillAsync();
        var first = await central.LeaseAsync(None);
        await data.ExecuteAsync("UPDATE hub.commands SET lease_until = now() - interval '1 second' WHERE id = @job", new { job });
        var second = await central.LeaseAsync(None);
        Assert.True(second!.FencingToken > first!.FencingToken);

        var error = await Assert.ThrowsAsync<CentralCallException>(() =>
            central.SaveCheckpointAsync(job, new EdgeCheckpointRequest(first.FencingToken, "access_logs", "999", null), None));
        Assert.Equal(HttpStatusCode.Conflict, error.Status);

        var reset = await Assert.ThrowsAsync<CentralCallException>(() =>
            central.ResetEpochAsync(job, new EdgeEpochResetRequest(first.FencingToken, 0, "access_logs"), None));
        Assert.Equal(HttpStatusCode.Conflict, reset.Status);
        Assert.Equal(0, (await StateAsync()).GetProperty("sourceEpoch").GetInt32());
    }

    [Fact]
    public async Task Cursor_never_moves_backwards()
    {
        var job = await RequestBackfillAsync();
        var leased = await central.LeaseAsync(None);

        await central.SaveCheckpointAsync(job, new EdgeCheckpointRequest(leased!.FencingToken, "access_logs", "100", 1), None);
        await central.SaveCheckpointAsync(job, new EdgeCheckpointRequest(leased.FencingToken, "access_logs", "50", 1), None);

        Assert.Equal("100", await data.ScalarAsync<string>("SELECT last_id::text FROM hub.device_event_cursors WHERE device_id = @device AND object_name = 'access_logs'", new { device }));
    }

    [Theory]
    [InlineData("pins", "1")]
    [InlineData("access_logs", "1;DROP")]
    [InlineData("access_logs", "-1")]
    public async Task Invalid_checkpoint_is_refused(string objectName, string lastId)
    {
        var job = await RequestBackfillAsync();
        var leased = await central.LeaseAsync(None);

        var error = await Assert.ThrowsAsync<CentralCallException>(() =>
            central.SaveCheckpointAsync(job, new EdgeCheckpointRequest(leased!.FencingToken, objectName, lastId, null), None));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    [Fact]
    public async Task Repeated_reset_request_opens_a_single_epoch()
    {
        var job = await RequestBackfillAsync();
        var leased = await central.LeaseAsync(None);
        var request = new EdgeEpochResetRequest(leased!.FencingToken, 0, "access_logs");

        await central.ResetEpochAsync(job, request, None);
        await central.ResetEpochAsync(job, request, None);

        Assert.Equal(1, (await StateAsync()).GetProperty("sourceEpoch").GetInt32());
    }

    [Theory]
    [InlineData(-300, "skewed", -310, -290)]
    [InlineData(300, "skewed", 290, 310)]
    [InlineData(0, "ok", -10, 10)]
    public async Task Clock_skew_is_measured_and_flagged(int offset, string status, int minimum, int maximum)
    {
        simulator.DeviceTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offset;

        var state0 = await StateAsync();
        Assert.Equal("unknown", state0.GetProperty("clockStatus").GetString());

        await RunBackfillAsync();

        var state = await StateAsync();
        Assert.Equal(status, state.GetProperty("clockStatus").GetString());
        Assert.InRange(state.GetProperty("clockSkewSeconds").GetInt32(), minimum, maximum);
    }

    [Fact]
    public async Task Original_device_time_is_kept_even_with_a_wrong_clock()
    {
        simulator.DeviceTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400;
        SeedLogs(1);

        await RunBackfillAsync();

        Assert.Equal("1700000001", await data.ScalarAsync<string>("SELECT raw_time FROM hub.access_events WHERE device_id = @device", new { device }));
    }

    [Fact]
    public async Task Sync_state_points_out_possibly_missing_logs()
    {
        await data.ExecuteAsync("UPDATE hub.devices SET reported_access_logs = 10 WHERE id = @device", new { device });
        SeedLogs(1, 2);

        await RunBackfillAsync();

        var state = await StateAsync();
        Assert.Equal(10, state.GetProperty("reportedAccessLogs").GetInt64());
        Assert.Equal(2, state.GetProperty("storedAccessLogs").GetInt64());
        Assert.True(state.GetProperty("possiblyMissing").GetBoolean());
    }

    [Fact]
    public async Task Due_backfill_is_scheduled_automatically_once_and_waits_for_the_interval()
    {
        SeedLogs(1, 2);

        await agent.RunCycleAsync(None);
        Assert.Equal(2, await StoredAsync());
        var after = await data.ScalarAsync<long>("SELECT count(*) FROM hub.commands WHERE device_id = @device AND kind = 'device.backfill'", new { device });
        Assert.Equal(1, after);

        await agent.RunCycleAsync(None);
        Assert.Equal(1, await data.ScalarAsync<long>("SELECT count(*) FROM hub.commands WHERE device_id = @device AND kind = 'device.backfill'", new { device }));

        await data.ExecuteAsync("UPDATE hub.commands SET created_at = now() - interval '1 day' WHERE device_id = @device", new { device });
        simulator.Tables["access_logs"].Add(Log(3));
        await agent.RunCycleAsync(None);
        Assert.Equal(3, await StoredAsync());
    }

    [Fact]
    public async Task Device_without_identity_or_credential_is_not_scheduled()
    {
        await data.ExecuteAsync("UPDATE hub.devices SET source_device_id = NULL WHERE id = @device", new { device });

        await agent.RunCycleAsync(None);

        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.commands WHERE device_id = @device AND kind = 'device.backfill'", new { device }));
        var refused = await admin.PostAsync(Url($"/api/v1/devices/{device}/backfill"), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task Second_request_while_one_is_pending_is_a_conflict()
    {
        await RequestBackfillAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(Url($"/api/v1/devices/{device}/backfill"), null)).StatusCode);
    }

    [Fact]
    public async Task Other_tenant_and_missing_permission_are_refused()
    {
        var other = await data.TenantAsync();
        using var stranger = host.AdminClient(await data.ActorAsync(other, Grants));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync(Url($"/api/v1/devices/{device}/backfill"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Url($"/api/v1/devices/{device}/sync-state"))).StatusCode);

        var slug = await data.ScalarAsync<string>("SELECT slug FROM hub.tenants WHERE id = @tenant", new { tenant });
        using var reader = host.AdminClient(await data.ActorAsync((tenant, slug), Permissions.DeviceRead));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(Url($"/api/v1/devices/{device}/backfill"), null)).StatusCode);
    }

    [Fact]
    public async Task Reading_beyond_one_job_limit_continues_in_the_next_job_without_loss()
    {
        // Mais do que 50 paginas de 200: o comando encerra pedindo continuacao, para o lease nao ficar preso.
        simulator.Tables["access_logs"] = Enumerable.Range(1, 10_050).Select(i => Log(i)).ToList();

        var job = await RunBackfillAsync();

        Assert.True(job.GetProperty("result").GetProperty("hasMore").GetBoolean());
        Assert.Equal(10_050, await StoredAsync());
        Assert.Equal(2L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.commands WHERE device_id = @device AND kind = 'device.backfill' AND status = 'completed'", new { device }));
        Assert.Equal("10050", await data.ScalarAsync<string>("SELECT last_id::text FROM hub.device_event_cursors WHERE device_id = @device AND object_name = 'access_logs'", new { device }));
    }

    [Fact]
    public async Task Endless_commands_cannot_hold_the_gateway_in_one_cycle()
    {
        var endless = new EndlessCentral();
        var options = new GatewayOptions();
        var gateway = new GatewayAgent(endless, new JobExecutor(options, NullLoggerFactory.Instance), options, NullLogger<GatewayAgent>.Instance);

        await gateway.RunCycleAsync(None);

        Assert.InRange(endless.Leases, 1, 25);
        Assert.Equal(endless.Leases, endless.Results);
    }

    private sealed class EndlessCentral : ICentralClient
    {
        public int Leases { get; private set; }

        public int Results { get; private set; }

        public Task<EdgeJob?> LeaseAsync(CancellationToken cancellationToken)
        {
            Leases++;
            return Task.FromResult<EdgeJob?>(new EdgeJob(Guid.NewGuid(), "tipo.desconhecido", 1, DateTimeOffset.UtcNow, null, null));
        }

        public Task SendResultAsync(Guid jobId, EdgeJobResult result, CancellationToken cancellationToken)
        {
            Results++;
            return Task.CompletedTask;
        }

        public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new HeartbeatResponse(DateTimeOffset.UtcNow));

        public Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EdgeDevice>> ListDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EdgeEventBatchResult> SendEventsAsync(EdgeEventBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveCheckpointAsync(Guid jobId, EdgeCheckpointRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EdgeEpochResetResponse> ResetEpochAsync(Guid jobId, EdgeEpochResetRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Interrupted_reading_resumes_from_the_last_saved_page()
    {
        simulator.Tables["access_logs"] = Enumerable.Range(1, 450).Select(i => Log(i)).ToList();
        var job = await RequestBackfillAsync();
        var leased = await central.LeaseAsync(None);

        // Grava so a primeira pagina e "cai".
        await central.SaveCheckpointAsync(job, new EdgeCheckpointRequest(leased!.FencingToken, "access_logs", "200", 1700000200), None);
        await data.ExecuteAsync("UPDATE hub.commands SET lease_until = now() - interval '1 second' WHERE id = @job", new { job });

        await agent.RunCycleAsync(None);

        Assert.Equal(250, await StoredAsync());
        Assert.Equal("450", await data.ScalarAsync<string>("SELECT last_id::text FROM hub.device_event_cursors WHERE device_id = @device AND object_name = 'access_logs'", new { device }));
    }
}
