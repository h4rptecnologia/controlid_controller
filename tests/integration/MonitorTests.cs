using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.ContractTests;
using Platform.EdgeGateway;
using Platform.Modules.DeviceIntegration.ControlId;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Authorization;

namespace Platform.IntegrationTests;

/// <summary>
/// Corpos copiados dos exemplos da documentacao oficial do Monitor, nao capturados de equipamento real.
/// O exemplo de catra_event usa aspas simples e o de operation_mode nao tem virgula: aqui vao as duas formas.
/// </summary>
internal static class MonitorSamples
{
    public const string SourceDeviceId = "478435";

    public const string Dao = """
        {"object_changes":[{"object":"access_logs","type":"inserted","values":{"id":"519","time":"1532977090","event":"7","device_id":"478435","identifier_id":"0","user_id":"12","portal_id":"1","identification_rule_id":"0","card_value":"99887766","log_type_id":"-1"}}],"device_id":478435}
        """;

    public const string DaoWithSecrets = """
        {"object_changes":[
          {"object":"access_logs","type":"inserted","values":{"id":"600","time":"1532977090","event":"6","user_id":"12","pin_value":"PINSECRETO","qrcode_value":"QRSECRETO","uhf_tag":"UHFSECRETO","card_value":"CARTAOSECRETO"}},
          {"object":"templates","type":"inserted","values":{"id":"7","user_id":"12","template":"BIOMETRIASECRETA"}},
          {"object":"cards","type":"inserted","values":{"id":"8","user_id":"12","value":"CARTAOSECRETO"}},
          {"object":"objeto_novo","type":"inserted","values":{"id":"1","qualquer":"VALORDESCONHECIDO"}}
        ],"device_id":478435}
        """;

    public const string Door = """{"door":{"id":1,"open":true},"access_event_id":15,"device_id":478435,"time":1575475894}""";

    public const string SecBox = """{"secbox":{"id":122641794705028910,"open":true},"access_event_id":15}""";

    public const string Catra = """{"event":{"type":7,"name":"TURN LEFT","time":1484126902,"uuid":"0e039178"},"access_event_id":15,"device_id":478435,"time":1484126902}""";

    public const string CatraAsDocumented = """{"event":{"type":7,"name":'TURN LEFT',"time":1484126902,"uuid":"0e039178"},"access_event_id":15,"device_id":478435,"time":1484126902}""";

    public const string OperationMode = """{"operation_mode":{"mode":0,"mode_name":"DEFAULT","time":1490271121,"last_offline":1490261121,"exception_mode":"none"},"device_id":478435}""";

    public const string Alive = """{"access_logs":42,"device_id":478435,"time":1739376235}""";

    public const string UsbDrive = """{"usb_drive":{"event":"Export formatted access logs routine succeeded"},"device_id":478435,"time":1490271121}""";
}

public sealed class MonitorNormalizerTests
{
    private static readonly Guid Device = Guid.NewGuid();

    private static IReadOnlyList<EdgeEvent>? Normalize(string route, string body) =>
        MonitorNormalizer.Normalize(route, Encoding.UTF8.GetBytes(body), Device, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Dao_keeps_allowed_fields_and_drops_card_value()
    {
        var item = Assert.Single(Normalize("dao", MonitorSamples.Dao)!);

        Assert.Equal(EdgeEventFamilies.AccessLogs, item.Family);
        Assert.Equal("519", item.SourceEventId);
        Assert.Equal("inserted", item.ChangeType);
        Assert.Equal("478435", item.SourceDeviceId);
        Assert.Equal(1532977090, item.OccurredAtUnix);
        Assert.Equal("7", item.Fields["event"]);
        Assert.False(item.Fields.ContainsKey("card_value"));
    }

    [Fact]
    public void Secrets_never_pass_and_unknown_objects_are_dropped()
    {
        var events = Normalize("dao", MonitorSamples.DaoWithSecrets)!;

        Assert.Equal(3, events.Count);
        var serialized = JsonSerializer.Serialize(events);
        foreach (var secret in new[] { "PINSECRETO", "QRSECRETO", "UHFSECRETO", "CARTAOSECRETO", "BIOMETRIASECRETA", "VALORDESCONHECIDO", "objeto_novo" })
        {
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Door_and_turnstile_follow_the_documented_shape()
    {
        var door = Assert.Single(Normalize("door", MonitorSamples.Door)!);
        Assert.Equal("true", door.Fields["open"]);
        Assert.Equal("15", door.Fields["access_event_id"]);
        Assert.Equal(1575475894, door.OccurredAtUnix);

        var catra = Assert.Single(Normalize("catra_event", MonitorSamples.Catra)!);
        Assert.Equal("TURN LEFT", catra.Fields["name"]);
        Assert.Equal("0e039178", catra.SourceUuid);
    }

    [Fact]
    public void Secbox_has_no_device_id_nor_time_as_documented_and_keeps_big_ids_exact()
    {
        var item = Assert.Single(Normalize("secbox", MonitorSamples.SecBox)!);

        Assert.Null(item.SourceDeviceId);
        Assert.Null(item.OccurredAtUnix);
        Assert.Equal("122641794705028910", item.Fields["id"]);
    }

    [Fact]
    public void Large_device_id_from_the_documentation_keeps_every_digit()
    {
        var item = Assert.Single(Normalize("device_is_alive", """{"access_logs":0,"device_id":6613047045004349,"time":1739376235}""")!);

        Assert.Equal("6613047045004349", item.SourceDeviceId);
    }

    [Theory]
    [InlineData("dao", "nao e json")]
    [InlineData("dao", "[1]")]
    [InlineData("dao", """{"device_id":1}""")]
    [InlineData("door", """{"device_id":1}""")]
    [InlineData("rota_inexistente", "{}")]
    public void Invalid_body_returns_null(string route, string body)
    {
        Assert.Null(Normalize(route, body));
    }

    [Fact]
    public void Documented_turnstile_example_with_single_quotes_is_not_valid_json()
    {
        // Registra a divergencia da doc: se o firmware enviar assim, o gateway recusa. Homologar com aparelho real.
        Assert.Null(Normalize("catra_event", MonitorSamples.CatraAsDocumented));
    }
}

public sealed class EventSpoolTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hub-spool-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static EdgeEvent Sample(string marker = "valor", DateTimeOffset? at = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), EdgeEventFamilies.Door, null, null, null, "1", 1, at ?? DateTimeOffset.UtcNow,
        new Dictionary<string, string> { ["id"] = marker });

    [Fact]
    public async Task Events_survive_a_restart_in_order()
    {
        var older = Sample("antigo", DateTimeOffset.UtcNow.AddMinutes(-1));
        var newer = Sample("novo");
        var spool = new EventSpool(directory);
        await spool.TryAppendAsync(newer, CancellationToken.None);
        await spool.TryAppendAsync(older, CancellationToken.None);

        var reopened = new EventSpool(directory);
        var batch = await reopened.ReadBatchAsync(10, CancellationToken.None);

        Assert.Equal(["antigo", "novo"], batch.Select(b => b.Event.Fields["id"]));
        Assert.Equal(2, reopened.Depth);
    }

    [Fact]
    public async Task Nothing_is_readable_on_disk()
    {
        var spool = new EventSpool(directory);
        await spool.TryAppendAsync(Sample("MARCADORVISIVEL"), CancellationToken.None);

        foreach (var file in Directory.EnumerateFiles(directory, "*.evt"))
        {
            Assert.DoesNotContain("MARCADORVISIVEL", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tampered_file_is_set_aside_without_blocking_the_rest()
    {
        var spool = new EventSpool(directory);
        await spool.TryAppendAsync(Sample("a", DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);
        await spool.TryAppendAsync(Sample("b"), CancellationToken.None);
        var first = Directory.EnumerateFiles(directory, "*.evt").Order(StringComparer.Ordinal).First();
        var bytes = await File.ReadAllBytesAsync(first);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(first, bytes);

        var batch = await spool.ReadBatchAsync(10, CancellationToken.None);

        Assert.Equal("b", Assert.Single(batch).Event.Fields["id"]);
        Assert.Equal(1, spool.Corrupted);
        Assert.Single(Directory.EnumerateFiles(directory, "*.corrupt"));
    }

    [Fact]
    public async Task Content_moved_to_another_file_name_fails_authentication()
    {
        var spool = new EventSpool(directory);
        await spool.TryAppendAsync(Sample(), CancellationToken.None);
        var file = Directory.EnumerateFiles(directory, "*.evt").Single();
        File.Move(file, Path.Combine(directory, "0000000000000001-outro.evt"));

        Assert.Empty(await spool.ReadBatchAsync(10, CancellationToken.None));
        Assert.Equal(1, spool.Corrupted);
    }

    [Fact]
    public async Task Full_spool_refuses_instead_of_overwriting()
    {
        var spool = new EventSpool(directory, maxEvents: 2);

        Assert.True(await spool.TryAppendAsync(Sample(), CancellationToken.None));
        Assert.True(await spool.TryAppendAsync(Sample(), CancellationToken.None));
        Assert.False(await spool.TryAppendAsync(Sample(), CancellationToken.None));
        Assert.Equal(2, spool.Depth);
    }
}

[Collection(PostgresTests.Name)]
public sealed class MonitorEndToEndTests : IDisposable
{
    private static readonly string[] Permissions_ =
    [
        Permissions.GatewaysRead, Permissions.GatewaysManage, Permissions.DeviceRead, Permissions.DeviceWrite,
        Permissions.DeviceProbe, Permissions.DeviceSettingsWrite, Permissions.EventsRead,
    ];

    private readonly TestData data;
    private readonly EdgeHost host;
    private readonly string spoolDirectory = Path.Combine(Path.GetTempPath(), "hub-spool-" + Guid.NewGuid().ToString("N"));
    private readonly string identityPath = Path.Combine(Path.GetTempPath(), "hub-gw-" + Guid.NewGuid().ToString("N") + ".pfx");
    private readonly List<WebApplication> listeners = [];

    public MonitorEndToEndTests(PostgresFixture postgres, StorageFixture storage)
    {
        data = new TestData(postgres.AdminOnHubConnection);
        host = new EdgeHost(postgres, storage);
    }

    public void Dispose()
    {
        foreach (var listener in listeners)
        {
            listener.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)listener).Dispose();
        }

        host.Dispose();
        File.Delete(identityPath);
        if (Directory.Exists(spoolDirectory))
        {
            Directory.Delete(spoolDirectory, recursive: true);
        }
    }

    private static CancellationToken None => CancellationToken.None;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private sealed record Rig(
        HttpClient Admin,
        Guid Tenant,
        Guid Device,
        Guid Gateway,
        GatewayAgent Agent,
        CentralClient Central,
        EventSpool Spool,
        DeviceDirectory Directory,
        MonitorCounters Counters,
        HttpClient Terminal,
        X509Certificate2 Identity) : IDisposable
    {
        public void Dispose()
        {
            Admin.Dispose();
            Central.Dispose();
            Terminal.Dispose();
            Identity.Dispose();
        }
    }

    private async Task<Rig> BuildRigAsync(bool verified = true, string host_ = "127.0.0.1")
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var admin = host.AdminClient(await data.ActorAsync(tenant, Permissions_));

        var created = await admin.PostAsJsonAsync(Url("/api/v1/gateways"), new { siteId = site, name = "Portaria" });
        var gateway = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var token = (await (await admin.PostAsync(Url($"/api/v1/gateways/{gateway}/enrollment-token"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var device = await admin.PostAsJsonAsync(Url("/api/v1/devices"), new { siteId = site, gatewayId = gateway, name = "Catraca", host = host_, port = 443, tlsPin = new string('A', 64) });
        var deviceId = (await device.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (verified)
        {
            await data.ExecuteAsync("UPDATE hub.devices SET source_device_id = @source::numeric WHERE id = @deviceId", new { source = MonitorSamples.SourceDeviceId, deviceId });
        }

        var identity = GatewayIdentity.LoadOrCreate(identityPath);
        var central = host.Central(identity);
        var options = new GatewayOptions { EnrollmentToken = token, ConnectorEnabled = false };
        var spool = new EventSpool(spoolDirectory);
        var directory = new DeviceDirectory();
        var agent = new GatewayAgent(central, new JobExecutor(options, NullLoggerFactory.Instance), options, NullLogger<GatewayAgent>.Instance, null, spool, directory);

        // Primeiro ciclo: registra o gateway e aprende os enderecos.
        await agent.RunCycleAsync(None);

        var counters = new MonitorCounters();
        var port = FreePort();
        var listener = MonitorListener.Build(port, "api/notifications", directory, spool, counters);
        await listener.StartAsync();
        listeners.Add(listener);

        return new Rig(admin, tenant.Id, deviceId, gateway, agent, central, spool, directory, counters, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") }, identity);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Task<HttpResponseMessage> PostAsync(Rig rig, string route, string body) =>
        rig.Terminal.PostAsync(Url($"api/notifications/{route}"), new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<List<JsonElement>> EventsAsync(Rig rig, string query = "")
    {
        var page = await (await rig.Admin.GetAsync(Url("/api/v1/events" + query))).Content.ReadFromJsonAsync<JsonElement>();
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Access_event_travels_from_terminal_to_query_api()
    {
        using var rig = await BuildRigAsync();

        var response = await PostAsync(rig, "dao", MonitorSamples.Dao);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, rig.Spool.Depth);

        await rig.Agent.DrainEventsAsync(None);

        Assert.Equal(0, rig.Spool.Depth);
        var item = Assert.Single(await EventsAsync(rig));
        Assert.Equal("access_granted", item.GetProperty("eventKind").GetString());
        Assert.Equal("7", item.GetProperty("sourceEventCode").GetString());
        Assert.Equal("519", item.GetProperty("sourceEventId").GetString());
        Assert.Equal("monitor", item.GetProperty("ingressSource").GetString());
        Assert.Equal(new DateTime(2018, 7, 30, 18, 58, 10, DateTimeKind.Utc), item.GetProperty("occurredAt").GetDateTime().ToUniversalTime());
        Assert.False(item.GetProperty("details").TryGetProperty("card_value", out _));
    }

    [Fact]
    public async Task Same_event_received_twice_is_stored_once()
    {
        using var rig = await BuildRigAsync();

        await PostAsync(rig, "dao", MonitorSamples.Dao);
        await PostAsync(rig, "dao", MonitorSamples.Dao);
        await rig.Agent.DrainEventsAsync(None);
        await PostAsync(rig, "dao", MonitorSamples.Dao);
        await rig.Agent.DrainEventsAsync(None);

        Assert.Single(await EventsAsync(rig));
        Assert.Equal(0, rig.Spool.Depth);
    }

    [Fact]
    public async Task Lost_acknowledgement_resends_the_batch_without_duplicating()
    {
        using var rig = await BuildRigAsync();
        var item = MonitorNormalizer.Normalize("dao", Encoding.UTF8.GetBytes(MonitorSamples.Dao), rig.Device, DateTimeOffset.UtcNow)!;
        var batch = new EdgeEventBatch(Guid.NewGuid(), item);

        var first = await rig.Central.SendEventsAsync(batch, None);
        var second = await rig.Central.SendEventsAsync(batch, None);

        Assert.Equal(first.Accepted, second.Accepted);
        Assert.Single(await EventsAsync(rig));
    }

    [Fact]
    public async Task Central_offline_keeps_events_in_the_spool_and_later_delivers_them()
    {
        using var rig = await BuildRigAsync();
        await PostAsync(rig, "dao", MonitorSamples.Dao);
        using var identity = GatewayIdentity.LoadOrCreate(identityPath);
        using var unreachable = new CentralClient(new Uri("https://127.0.0.1:1/"), identity, host.Root);
        var options = new GatewayOptions();
        var offline = new GatewayAgent(unreachable, new JobExecutor(options, NullLoggerFactory.Instance), options, NullLogger<GatewayAgent>.Instance, null, rig.Spool);

        await Assert.ThrowsAsync<CentralCallException>(() => offline.DrainEventsAsync(None));
        Assert.Equal(1, rig.Spool.Depth);

        await rig.Agent.DrainEventsAsync(None);
        Assert.Equal(0, rig.Spool.Depth);
        Assert.Single(await EventsAsync(rig));
    }

    [Fact]
    public async Task Source_outside_the_device_list_is_refused_before_anything_is_written()
    {
        using var rig = await BuildRigAsync(host_: "192.0.2.50");

        var response = await PostAsync(rig, "dao", MonitorSamples.Dao);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, rig.Spool.Depth);
        Assert.Equal(1, rig.Counters.Get("unknown_source"));
    }

    [Fact]
    public async Task Forged_device_id_is_refused_by_the_central_and_audited()
    {
        using var rig = await BuildRigAsync();
        var forged = MonitorSamples.Dao.Replace("478435", "999999", StringComparison.Ordinal);

        await PostAsync(rig, "dao", forged);
        await rig.Agent.DrainEventsAsync(None);

        Assert.Empty(await EventsAsync(rig));
        Assert.Equal(0, rig.Spool.Depth);
        Assert.Equal(1, rig.Spool.Dropped);
        Assert.Equal(1L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.audit_log WHERE tenant_id = @Tenant AND action = 'edge.events_rejected' AND metadata::text LIKE '%device_id_mismatch%'", new { rig.Tenant }));
    }

    [Fact]
    public async Task Device_never_probed_has_no_identity_to_compare_and_is_refused()
    {
        using var rig = await BuildRigAsync(verified: false);

        await PostAsync(rig, "dao", MonitorSamples.Dao);
        await rig.Agent.DrainEventsAsync(None);

        Assert.Empty(await EventsAsync(rig));
    }

    [Fact]
    public async Task Gateway_cannot_send_events_for_a_device_of_another_gateway()
    {
        using var rig = await BuildRigAsync();
        var other = Guid.NewGuid();
        var batch = new EdgeEventBatch(Guid.NewGuid(), MonitorNormalizer.Normalize("door", Encoding.UTF8.GetBytes(MonitorSamples.Door), other, DateTimeOffset.UtcNow)!);

        var result = await rig.Central.SendEventsAsync(batch, None);

        Assert.Equal("device_not_of_gateway", Assert.Single(result.Rejected).Code);
        Assert.Empty(result.Accepted);
    }

    [Fact]
    public async Task Central_refuses_secret_fields_even_from_a_compromised_gateway()
    {
        using var rig = await BuildRigAsync();
        var evil = new EdgeEvent(Guid.NewGuid(), rig.Device, EdgeEventFamilies.AccessLogs, "inserted", "1", null, MonitorSamples.SourceDeviceId, 1, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["id"] = "1", ["pin_value"] = "PINSECRETO" });

        var result = await rig.Central.SendEventsAsync(new EdgeEventBatch(Guid.NewGuid(), [evil]), None);

        Assert.Equal("field_not_allowed", Assert.Single(result.Rejected).Code);
        Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.access_events t WHERE t::text LIKE '%PINSECRETO%'"));
    }

    [Fact]
    public async Task Unknown_family_is_refused_by_the_central()
    {
        using var rig = await BuildRigAsync();
        var evil = new EdgeEvent(Guid.NewGuid(), rig.Device, "pins", null, null, null, MonitorSamples.SourceDeviceId, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>());

        var result = await rig.Central.SendEventsAsync(new EdgeEventBatch(Guid.NewGuid(), [evil]), None);

        Assert.Equal("unknown_family", Assert.Single(result.Rejected).Code);
    }

    [Fact]
    public async Task Secrets_from_the_terminal_reach_neither_disk_nor_database()
    {
        using var rig = await BuildRigAsync();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig, "dao", MonitorSamples.DaoWithSecrets)).StatusCode);
        await rig.Agent.DrainEventsAsync(None);

        var events = await EventsAsync(rig);
        Assert.Equal(3, events.Count);
        foreach (var secret in new[] { "PINSECRETO", "QRSECRETO", "UHFSECRETO", "CARTAOSECRETO", "BIOMETRIASECRETA", "VALORDESCONHECIDO" })
        {
            Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.access_events t WHERE t::text LIKE '%' || @secret || '%'", new { secret }));
        }
    }

    [Theory]
    [InlineData("template")]
    [InlineData("user_image")]
    [InlineData("face_template")]
    [InlineData("card")]
    [InlineData("pin")]
    [InlineData("password")]
    [InlineData("access_photo")]
    public async Task Sensitive_routes_answer_without_storing_anything(string route)
    {
        using var rig = await BuildRigAsync();

        var response = await PostAsync(rig, route, """{"device_id":478435,"value":"SEGREDOENVIADO"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, rig.Spool.Depth);
        Assert.Equal(1, rig.Counters.Get("discarded_sensitive"));
    }

    [Theory]
    [InlineData("door", "door_opened")]
    [InlineData("catra_event", "turnstile_turn_left")]
    [InlineData("operation_mode", "operation_mode_changed")]
    [InlineData("usb_drive", "usb_drive")]
    public async Task Other_routes_are_classified(string route, string kind)
    {
        using var rig = await BuildRigAsync();
        var body = route switch
        {
            "door" => MonitorSamples.Door,
            "catra_event" => MonitorSamples.Catra,
            "operation_mode" => MonitorSamples.OperationMode,
            _ => MonitorSamples.UsbDrive,
        };

        await PostAsync(rig, route, body);
        await rig.Agent.DrainEventsAsync(None);

        Assert.Equal(kind, Assert.Single(await EventsAsync(rig)).GetProperty("eventKind").GetString());
    }

    [Fact]
    public async Task Secbox_without_time_keeps_two_real_openings_apart_and_records_only_when_it_was_seen()
    {
        using var rig = await BuildRigAsync();

        await PostAsync(rig, "secbox", MonitorSamples.SecBox);
        await PostAsync(rig, "secbox", MonitorSamples.SecBox);
        await rig.Agent.DrainEventsAsync(None);

        var events = await EventsAsync(rig);
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(JsonValueKind.Null, e.GetProperty("occurredAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, e.GetProperty("observedAt").ValueKind);
        });
    }

    [Fact]
    public async Task Alive_updates_the_device_without_becoming_history()
    {
        using var rig = await BuildRigAsync();

        await PostAsync(rig, "device_is_alive", MonitorSamples.Alive);
        await rig.Agent.DrainEventsAsync(None);

        Assert.Empty(await EventsAsync(rig));
        var device = await (await rig.Admin.GetAsync(Url($"/api/v1/devices/{rig.Device}"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reachable", device.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, device.GetProperty("lastSeenAt").ValueKind);
        Assert.Equal(42L, await data.ScalarAsync<long>("SELECT reported_access_logs FROM hub.devices WHERE id = @Device", new { rig.Device }));
    }

    [Theory]
    [InlineData("99", "unknown")]
    [InlineData("6", "access_denied")]
    public async Task Unknown_code_is_kept_as_it_came_and_not_guessed(string code, string kind)
    {
        using var rig = await BuildRigAsync();
        var body = MonitorSamples.Dao.Replace("\"event\":\"7\"", $"\"event\":\"{code}\"", StringComparison.Ordinal);

        await PostAsync(rig, "dao", body);
        await rig.Agent.DrainEventsAsync(None);

        var item = Assert.Single(await EventsAsync(rig));
        Assert.Equal(kind, item.GetProperty("eventKind").GetString());
        Assert.Equal(code, item.GetProperty("sourceEventCode").GetString());
    }

    [Fact]
    public async Task Invalid_body_and_unknown_route_write_nothing()
    {
        using var rig = await BuildRigAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig, "dao", "nao e json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig, "catra_event", MonitorSamples.CatraAsDocumented)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig, "inventada", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Terminal.PostAsync(Url("outro/dao"), new StringContent("{}"))).StatusCode);
        Assert.Equal(0, rig.Spool.Depth);
    }

    [Fact]
    public async Task Other_tenant_and_other_site_cannot_read_the_events()
    {
        using var rig = await BuildRigAsync();
        await PostAsync(rig, "dao", MonitorSamples.Dao);
        await rig.Agent.DrainEventsAsync(None);

        var otherTenant = await data.TenantAsync();
        using var stranger = host.AdminClient(await data.ActorAsync(otherTenant, Permissions.EventsRead));
        var page = await (await stranger.GetAsync(Url("/api/v1/events"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());

        var ownSlug = await data.ScalarAsync<string>("SELECT slug FROM hub.tenants WHERE id = @Tenant", new { rig.Tenant });
        var otherSite = await data.SiteAsync(rig.Tenant, "Outra");
        var scoped = await data.ActorAsync((rig.Tenant, ownSlug));
        await data.GrantAsync(scoped, Permissions.EventsRead, otherSite);
        using var limited = host.AdminClient(scoped);
        Assert.Equal(0, (await (await limited.GetAsync(Url("/api/v1/events"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());

        using var noPermission = host.AdminClient(await data.ActorAsync((rig.Tenant, ownSlug)));
        Assert.Equal(HttpStatusCode.Forbidden, (await noPermission.GetAsync(Url("/api/v1/events"))).StatusCode);
    }

    [Fact]
    public async Task Event_query_walks_every_page_once_and_filters()
    {
        using var rig = await BuildRigAsync();
        foreach (var id in Enumerable.Range(1, 5))
        {
            await PostAsync(rig, "dao", MonitorSamples.Dao.Replace("\"id\":\"519\"", $"\"id\":\"{id}\"", StringComparison.Ordinal));
        }

        await PostAsync(rig, "door", MonitorSamples.Door);
        await rig.Agent.DrainEventsAsync(None);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var path = "/api/v1/events?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await (await rig.Admin.GetAsync(Url(path))).Content.ReadFromJsonAsync<JsonElement>();
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(6, seen.Distinct().Count());
        Assert.Single(await EventsAsync(rig, "?eventKind=door_opened"));
        Assert.Equal(5, (await EventsAsync(rig, "?eventKind=access_granted")).Count);
    }

    [Fact]
    public async Task Full_spool_answers_503_so_the_event_is_not_acknowledged()
    {
        using var rig = await BuildRigAsync();
        var fullDirectory = Path.Combine(Path.GetTempPath(), "hub-spool-" + Guid.NewGuid().ToString("N"));
        var full = new EventSpool(fullDirectory, maxEvents: 0);
        var port = FreePort();
        var listener = MonitorListener.Build(port, "api/notifications", rig.Directory, full, rig.Counters);
        await listener.StartAsync();
        listeners.Add(listener);
        using var terminal = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        var response = await terminal.PostAsync(Url("api/notifications/dao"), new StringContent(MonitorSamples.Dao, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, rig.Counters.Get("spool_full"));
        Directory.Delete(fullDirectory, recursive: true);
    }

    [Fact]
    public async Task Oversized_body_is_refused_and_writes_nothing()
    {
        using var rig = await BuildRigAsync();
        var huge = "{\"object_changes\":[],\"x\":\"" + new string('a', 2 * 1024 * 1024) + "\"}";

        var response = await PostAsync(rig, "dao", huge);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, rig.Spool.Depth);
    }

    private async Task<(ControlIdSimulator Device, X509Certificate2 Certificate, Guid DeviceId, Guid Job, Rig Rig)> ProbeSetupAsync(string password)
    {
        var rig = await BuildRigAsync(verified: false);
        var certificate = TestCertificates.SelfSigned();
        var simulator = await ControlIdSimulator.StartAsync(certificate);
        var site = await data.ScalarAsync<Guid>("SELECT site_id FROM hub.devices WHERE id = @Device", new { rig.Device });
        var created = await rig.Admin.PostAsJsonAsync(Url("/api/v1/devices"), new { siteId = site, gatewayId = rig.Gateway, name = "Real", host = "127.0.0.1", port = simulator.Address.Port, tlsPin = TestCertificates.Pin(certificate) });
        var deviceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await rig.Admin.PutAsJsonAsync(Url($"/api/v1/devices/{deviceId}/credential"), new { login = ControlIdSimulator.ValidLogin, password });
        var job = (await (await rig.Admin.PostAsync(Url($"/api/v1/devices/{deviceId}/probe"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (simulator, certificate, deviceId, job, rig);
    }

    private static GatewayAgent ProbingAgent(Rig rig)
    {
        var options = new GatewayOptions { ConnectorEnabled = true, AllowedCidrs = ["127.0.0.0/8"] };
        return new GatewayAgent(rig.Central, new JobExecutor(options, NullLoggerFactory.Instance), options, NullLogger<GatewayAgent>.Instance);
    }

    [Fact]
    public async Task Successful_probe_teaches_the_device_id_used_to_verify_events()
    {
        var (simulator, certificate, deviceId, _, rig) = await ProbeSetupAsync(ControlIdSimulator.ValidPassword);
        await using (simulator)
        using (certificate)
        using (rig)
        {
            await ProbingAgent(rig).RunCycleAsync(None);

            Assert.Equal("9007199254740993", await data.ScalarAsync<string>("SELECT source_device_id::text FROM hub.devices WHERE id = @deviceId", new { deviceId }));
        }
    }

    [Fact]
    public async Task Probe_with_rejected_login_teaches_nothing()
    {
        var (simulator, certificate, deviceId, _, rig) = await ProbeSetupAsync("senha-errada");
        await using (simulator)
        using (certificate)
        using (rig)
        {
            await ProbingAgent(rig).RunCycleAsync(None);

            Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.devices WHERE id = @deviceId AND source_device_id IS NOT NULL", new { deviceId }));
        }
    }

    [Fact]
    public async Task Learned_device_id_is_not_overwritten_by_a_later_report()
    {
        var (simulator, certificate, deviceId, job, rig) = await ProbeSetupAsync(ControlIdSimulator.ValidPassword);
        await using (simulator)
        using (certificate)
        using (rig)
        {
            await data.ExecuteAsync("UPDATE hub.devices SET source_device_id = 111 WHERE id = @deviceId", new { deviceId });

            await ProbingAgent(rig).RunCycleAsync(None);

            Assert.Equal("completed", await data.ScalarAsync<string>("SELECT status FROM hub.commands WHERE id = @job", new { job }));
            Assert.Equal("111", await data.ScalarAsync<string>("SELECT source_device_id::text FROM hub.devices WHERE id = @deviceId", new { deviceId }));
        }
    }

    [Fact]
    public async Task Event_without_declared_id_from_an_unverified_device_is_refused()
    {
        using var rig = await BuildRigAsync(verified: false);

        await PostAsync(rig, "secbox", MonitorSamples.SecBox);
        await rig.Agent.DrainEventsAsync(None);

        Assert.Empty(await EventsAsync(rig));
        Assert.Equal(1, rig.Spool.Dropped);
    }

    [Fact]
    public async Task Report_of_rejected_login_cannot_teach_an_identity_even_if_it_carries_one()
    {
        var (simulator, certificate, deviceId, job, rig) = await ProbeSetupAsync(ControlIdSimulator.ValidPassword);
        await using (simulator)
        using (certificate)
        using (rig)
        {
            var leased = await rig.Central.LeaseAsync(None);
            while (leased!.Id != job)
            {
                await rig.Central.SendResultAsync(leased.Id, new EdgeJobResult(leased.FencingToken, EdgeJobOutcomes.Failed, "x", null), None);
                leased = await rig.Central.LeaseAsync(None);
            }

            var forged = new ProbeReport("Reachable", "Verified", "Rejected", null, null, "424242", null, null, null, DateTimeOffset.UtcNow);
            await rig.Central.SendResultAsync(job, new EdgeJobResult(leased.FencingToken, EdgeJobOutcomes.Succeeded, null, forged), None);

            Assert.Equal(0L, await data.ScalarAsync<long>("SELECT count(*) FROM hub.devices WHERE id = @deviceId AND source_device_id IS NOT NULL", new { deviceId }));
        }
    }

    [Fact]
    public void Allowed_lists_never_include_a_field_classified_as_sensitive()
    {
        foreach (var family in new[] { EdgeEventFamilies.AccessLogs, EdgeEventFamilies.AlarmLogs, EdgeEventFamilies.Templates, EdgeEventFamilies.Cards })
        {
            var definition = ObjectCatalog.Objects[family];
            Assert.Empty(EdgeEventFamilies.AllowedFields[family].Intersect(definition.Sensitive));
            Assert.Empty(EdgeEventFamilies.AllowedFields[family].Intersect(definition.Never));
        }
    }
}
