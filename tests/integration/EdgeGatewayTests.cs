using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.ContractTests;
using Platform.EdgeGateway;
using Platform.Modules.Fleet;
using Platform.Shared.Contracts;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Http;

namespace Platform.IntegrationTests;

/// <summary>API em Kestrel real com mTLS, para o certificado de cliente passar pelo handshake de verdade.</summary>
public sealed class EdgeHost : IDisposable
{
    private readonly string certificatePath = Path.Combine(Path.GetTempPath(), "hub-edge-" + Guid.NewGuid().ToString("N") + ".pfx");

    public EdgeHost(PostgresFixture postgres, StorageFixture storage)
    {
        Root = TestCertificates.Authority();
        using var server = TestCertificates.IssuedBy(Root, "127.0.0.1");
        File.WriteAllBytes(certificatePath, server.Export(X509ContentType.Pfx));

        PublicPort = FreePort();
        EdgePort = FreePort();
        Factory = new HubApiFactory(postgres.AppConnection, new Dictionary<string, string?>
        {
            ["Edge:Port"] = EdgePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:PublicPort"] = PublicPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:CertificatePath"] = certificatePath,
            ["Kms:Address"] = storage.KmsAddress.ToString(),
            ["Kms:Token"] = storage.Token,
            ["Storage:ServiceUrl"] = storage.S3Address.ToString(),
            ["Storage:Bucket"] = StorageFixture.Bucket,
            ["Storage:AccessKey"] = storage.AccessKey,
            ["Storage:SecretKey"] = storage.SecretKey,
            ["RateLimit:PerMinute"] = "100000",
            ["RateLimit:SensitivePerMinute"] = "100000",
        });
        Factory.UseKestrel();
        Factory.StartServer();
    }

    public X509Certificate2 Root { get; }

    public int PublicPort { get; }

    public int EdgePort { get; }

    public HubApiFactory Factory { get; }

    public Uri EdgeAddress => new($"https://127.0.0.1:{EdgePort}/");

    public HttpClient AdminClient(TestActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
#pragma warning disable CA2000 // O HttpClient descarta o handler.
        var client = new HttpClient(new AutoIdempotencyHandler { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri($"http://127.0.0.1:{PublicPort}/") };
#pragma warning restore CA2000
        client.DefaultRequestHeaders.Add("X-Test-Sub", actor.Subject);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", actor.TenantSlug);
        client.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        client.DefaultRequestHeaders.Add(RequestPipeline.CsrfHeader, "1");
        return client;
    }

    public CentralClient Central(X509Certificate2 identity) => new(EdgeAddress, identity, Root);

    public void Dispose()
    {
        Factory.Dispose();
        Root.Dispose();
        File.Delete(certificatePath);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[Collection(PostgresTests.Name)]
public sealed class EdgeGatewayTests : IDisposable
{
    private static readonly string[] AllPermissions =
    [
        Permissions.GatewaysRead, Permissions.GatewaysManage, Permissions.DeviceRead, Permissions.DeviceWrite,
        Permissions.DeviceProbe, Permissions.DeviceSettingsWrite,
    ];

    private readonly TestData data;
    private readonly EdgeHost stack;
    private readonly List<string> identityFiles = [];

    public EdgeGatewayTests(PostgresFixture postgres, StorageFixture storage)
    {
        data = new TestData(postgres.AdminOnHubConnection);
        stack = new EdgeHost(postgres, storage);
    }

    public void Dispose()
    {
        stack.Dispose();
        foreach (var file in identityFiles)
        {
            File.Delete(file);
        }
    }

    private static CancellationToken None => CancellationToken.None;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private X509Certificate2 NewIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), "hub-gw-" + Guid.NewGuid().ToString("N") + ".pfx");
        identityFiles.Add(path);
        return GatewayIdentity.LoadOrCreate(path);
    }

    private async Task<(TestActor Admin, Guid Site, HttpClient Client)> TenantWithAdminAsync()
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var admin = await data.ActorAsync(tenant, AllPermissions);
        return (admin, site, stack.AdminClient(admin));
    }

    private static async Task<Guid> CreateGatewayAsync(HttpClient admin, Guid site)
    {
        var response = await admin.PostAsJsonAsync(Url("/api/v1/gateways"), new { siteId = site, name = "Portaria" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string> NewTokenAsync(HttpClient admin, Guid gateway)
    {
        var response = await admin.PostAsync(Url($"/api/v1/gateways/{gateway}/enrollment-token"), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<(Guid Gateway, CentralClient Central, X509Certificate2 Identity)> EnrolledGatewayAsync(HttpClient admin, Guid site)
    {
        var gateway = await CreateGatewayAsync(admin, site);
        var identity = NewIdentity();
        var central = stack.Central(identity);
        await central.EnrollAsync(new EnrollRequest(await NewTokenAsync(admin, gateway), "1.0"), None);
        return (gateway, central, identity);
    }

    private static async Task<Guid> CreateDeviceAsync(HttpClient admin, Guid site, Guid gateway, string host = "127.0.0.1", int port = 443, string? pin = null)
    {
        var response = await admin.PostAsJsonAsync(Url("/api/v1/devices"), new { siteId = site, gatewayId = gateway, name = "Catraca", host, port, tlsMode = "pinned", tlsPin = pin ?? new string('A', 64) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> RequestProbeAsync(HttpClient admin, Guid device)
    {
        var response = await admin.PostAsync(Url($"/api/v1/devices/{device}/probe"), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // Identidade

    [Fact]
    public async Task Enrolled_gateway_authenticates_and_reports_health()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var response = await central.HeartbeatAsync(new HeartbeatRequest("1.2.3", 4, DateTimeOffset.UtcNow.AddSeconds(-120)), None);

            Assert.InRange(response.ServerTimeUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            var row = (await (await admin.GetAsync(Url("/api/v1/gateways"))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == gateway);
            Assert.Equal("active", row.GetProperty("status").GetString());
            Assert.Equal("1.2.3", row.GetProperty("version").GetString());
            Assert.Equal(4, row.GetProperty("spoolDepth").GetInt32());
            Assert.InRange(row.GetProperty("clockSkewSeconds").GetInt32(), -130, -110);
            Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastSeenAt").ValueKind);
        }
    }

    [Fact]
    public async Task Token_works_once_and_a_second_identity_is_refused()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var gateway = await CreateGatewayAsync(admin, site);
        var token = await NewTokenAsync(admin, gateway);
        using var first = NewIdentity();
        using var second = NewIdentity();
        using var firstCentral = stack.Central(first);
        using var secondCentral = stack.Central(second);

        await firstCentral.EnrollAsync(new EnrollRequest(token, null), None);

        var error = await Assert.ThrowsAsync<CentralCallException>(() => secondCentral.EnrollAsync(new EnrollRequest(token, null), None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        await Assert.ThrowsAsync<CentralCallException>(() => secondCentral.HeartbeatAsync(new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow), None));
    }

    [Fact]
    public async Task Simultaneous_enrollments_with_one_token_activate_only_one_identity()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var gateway = await CreateGatewayAsync(admin, site);
        var token = await NewTokenAsync(admin, gateway);
        var identities = Enumerable.Range(0, 6).Select(_ => NewIdentity()).ToList();
        var clients = identities.Select(stack.Central).ToList();

        var outcomes = await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                await c.EnrollAsync(new EnrollRequest(token, null), None);
                return true;
            }
            catch (CentralCallException)
            {
                return false;
            }
        }));

        Assert.Single(outcomes, ok => ok);
        clients.ForEach(c => c.Dispose());
        identities.ForEach(i => i.Dispose());
    }

    [Fact]
    public async Task Expired_superseded_and_invalid_tokens_get_the_same_refusal()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        using var identity = NewIdentity();
        using var central = stack.Central(identity);

        var expiredGateway = await CreateGatewayAsync(admin, site);
        var expired = await NewTokenAsync(admin, expiredGateway);
        await data.ExecuteAsync("UPDATE hub.edge_enrollments SET expires_at = now() - interval '1 minute' WHERE gateway_id = @expiredGateway", new { expiredGateway });

        var replacedGateway = await CreateGatewayAsync(admin, site);
        var superseded = await NewTokenAsync(admin, replacedGateway);
        await NewTokenAsync(admin, replacedGateway);

        foreach (var token in new[] { expired, superseded, "token-inventado" })
        {
            var error = await Assert.ThrowsAsync<CentralCallException>(() => central.EnrollAsync(new EnrollRequest(token, null), None));
            Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        }
    }

    [Fact]
    public async Task Database_keeps_only_a_hash_of_the_token()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var gateway = await CreateGatewayAsync(admin, site);
        var token = await NewTokenAsync(admin, gateway);

        Assert.Equal(0L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.edge_enrollments WHERE gateway_id = @gateway AND position(convert_to(@token, 'UTF8') IN token_hash) > 0", new { gateway, token }));
        Assert.Equal(1L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.edge_enrollments WHERE gateway_id = @gateway AND token_hash = @hash", new { gateway, hash = EdgeEndpoints.HashToken(token) }));
        Assert.Equal(0L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.idempotency_keys WHERE position(convert_to(@token, 'UTF8') IN coalesce(response_body, ''::bytea)) > 0", new { token }));
    }

    [Fact]
    public async Task Unknown_identity_is_rejected()
    {
        using var identity = NewIdentity();
        using var central = stack.Central(identity);

        var error = await Assert.ThrowsAsync<CentralCallException>(() => central.HeartbeatAsync(new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow), None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
    }

    [Fact]
    public async Task Edge_port_refuses_connections_without_client_certificate()
    {
#pragma warning disable CA5359, CA5399 // So para chegar ate o handshake do servidor: o teste e sobre o certificado de cliente.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
#pragma warning restore CA5359, CA5399
        using var client = new HttpClient(handler) { BaseAddress = stack.EdgeAddress };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsJsonAsync(Url("edge/v1/heartbeat"), new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Edge_routes_on_the_public_port_have_no_identity()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{stack.PublicPort}/") };

        var response = await client.PostAsJsonAsync(Url("edge/v1/heartbeat"), new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var enroll = await client.PostAsJsonAsync(Url("edge/v1/enroll"), new EnrollRequest("x", null));
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
    }

    [Fact]
    public async Task Operator_session_does_not_authenticate_on_edge_routes()
    {
        var (_, _, admin) = await TenantWithAdminAsync();

        var response = await admin.PostAsJsonAsync(Url("/edge/v1/heartbeat"), new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Gateway_identity_does_not_grant_operator_routes()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (_, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        using (var handler = new SocketsHttpHandler())
        {
            handler.SslOptions.ClientCertificates = [identity];
            var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            chain.CustomTrustStore.Add(stack.Root);
            handler.SslOptions.CertificateChainPolicy = chain;
            using var client = new HttpClient(handler) { BaseAddress = stack.EdgeAddress };

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url("api/v1/sites"))).StatusCode);
        }
    }

    [Fact]
    public async Task Expired_gateway_certificate_cannot_enroll_nor_authenticate_even_with_a_known_key()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var gateway = await CreateGatewayAsync(admin, site);
        using var expired = TestCertificates.SelfSigned(validFor: TimeSpan.FromHours(1), startOffset: TimeSpan.FromHours(-2));
        using var central = stack.Central(expired);

        var enroll = await Assert.ThrowsAsync<CentralCallException>(async () => await central.EnrollAsync(new EnrollRequest(await NewTokenAsync(admin, gateway), null), None));
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.Status);

        // Chave ja cadastrada, com o certificado vencido.
        await data.ExecuteAsync(
            "UPDATE hub.edge_gateways SET status = 'active', identity_thumbprint = @thumbprint WHERE id = @gateway",
            new { thumbprint = EdgeIdentity.ThumbprintOf(expired), gateway });
        var heartbeat = await Assert.ThrowsAsync<CentralCallException>(() => central.HeartbeatAsync(new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow), None));
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.Status);
    }

    [Fact]
    public async Task Revoked_gateway_loses_access_immediately()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            await central.HeartbeatAsync(new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow), None);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync(Url($"/api/v1/gateways/{gateway}/revoke"), null)).StatusCode);

            var error = await Assert.ThrowsAsync<CentralCallException>(() => central.HeartbeatAsync(new HeartbeatRequest(null, 0, DateTimeOffset.UtcNow), None));
            Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        }
    }

    [Fact]
    public async Task Same_key_cannot_enroll_a_second_gateway()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (_, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var other = await CreateGatewayAsync(admin, site);
            var error = await Assert.ThrowsAsync<CentralCallException>(async () => await central.EnrollAsync(new EnrollRequest(await NewTokenAsync(admin, other), null), None));
            Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        }
    }

    [Fact]
    public async Task App_role_cannot_list_gateways_of_other_tenants_through_functions()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (_, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var result = await data.ScalarAsync<long>("SELECT count(*) FROM hub.resolve_gateway('thumbprint-inexistente')");
            Assert.Equal(0L, result);
        }
    }

    // Fila

    [Fact]
    public async Task Gateway_only_sees_its_own_commands()
    {
        var (_, siteA, adminA) = await TenantWithAdminAsync();
        var (_, siteB, adminB) = await TenantWithAdminAsync();
        var (gatewayA, centralA, identityA) = await EnrolledGatewayAsync(adminA, siteA);
        var (_, centralB, identityB) = await EnrolledGatewayAsync(adminB, siteB);
        using (centralA)
        using (identityA)
        using (centralB)
        using (identityB)
        {
            var device = await CreateDeviceAsync(adminA, siteA, gatewayA);
            var job = await RequestProbeAsync(adminA, device);

            Assert.Null(await centralB.LeaseAsync(None));

            var leased = await centralA.LeaseAsync(None);
            Assert.Equal(job, leased!.Id);

            var error = await Assert.ThrowsAsync<CentralCallException>(() =>
                centralB.SendResultAsync(job, new EdgeJobResult(leased.FencingToken, EdgeJobOutcomes.Failed, "forjado", null), None));
            Assert.Equal(HttpStatusCode.Conflict, error.Status);
        }
    }

    [Fact]
    public async Task Gateway_of_the_same_tenant_cannot_take_or_answer_another_gateways_command()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gatewayA, centralA, identityA) = await EnrolledGatewayAsync(admin, site);
        var (_, centralB, identityB) = await EnrolledGatewayAsync(admin, site);
        using (centralA)
        using (identityA)
        using (centralB)
        using (identityB)
        {
            var job = await RequestProbeAsync(admin, await CreateDeviceAsync(admin, site, gatewayA));

            Assert.Null(await centralB.LeaseAsync(None));

            var leased = await centralA.LeaseAsync(None);
            var error = await Assert.ThrowsAsync<CentralCallException>(() =>
                centralB.SendResultAsync(job, new EdgeJobResult(leased!.FencingToken, EdgeJobOutcomes.Failed, "forjado", null), None));
            Assert.Equal(HttpStatusCode.Conflict, error.Status);
            Assert.Equal("leased", await data.ScalarAsync<string>("SELECT status FROM hub.commands WHERE id = @job", new { job }));
        }
    }

    [Fact]
    public async Task One_command_per_device_at_a_time()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var device = await CreateDeviceAsync(admin, site, gateway);
            var other = await CreateDeviceAsync(admin, site, gateway, "127.0.0.2");
            await RequestProbeAsync(admin, device);
            await RequestProbeAsync(admin, device);
            await RequestProbeAsync(admin, other);

            var first = await central.LeaseAsync(None);
            var second = await central.LeaseAsync(None);
            var third = await central.LeaseAsync(None);

            Assert.Equal(device, first!.Device!.DeviceId);
            Assert.Equal(other, second!.Device!.DeviceId);
            Assert.Null(third);

            await central.SendResultAsync(first.Id, new EdgeJobResult(first.FencingToken, EdgeJobOutcomes.Failed, "x", null), None);
            Assert.Equal(device, (await central.LeaseAsync(None))!.Device!.DeviceId);
        }
    }

    [Fact]
    public async Task Expired_lease_of_repeatable_command_returns_with_new_fencing_and_old_result_is_refused()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var job = await RequestProbeAsync(admin, await CreateDeviceAsync(admin, site, gateway));
            var first = await central.LeaseAsync(None);
            await data.ExecuteAsync("UPDATE hub.commands SET lease_until = now() - interval '1 second' WHERE id = @job", new { job });

            var second = await central.LeaseAsync(None);

            Assert.Equal(job, second!.Id);
            Assert.True(second.FencingToken > first!.FencingToken);

            var stale = await Assert.ThrowsAsync<CentralCallException>(() =>
                central.SendResultAsync(job, new EdgeJobResult(first.FencingToken, EdgeJobOutcomes.Succeeded, null, null), None));
            Assert.Equal(HttpStatusCode.Conflict, stale.Status);

            await central.SendResultAsync(job, new EdgeJobResult(second.FencingToken, EdgeJobOutcomes.Succeeded, null, null), None);
            await central.SendResultAsync(job, new EdgeJobResult(second.FencingToken, EdgeJobOutcomes.Succeeded, null, null), None);
        }
    }

    [Fact]
    public async Task Lost_lease_of_non_repeatable_command_becomes_unknown_and_is_never_delivered_again()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var job = await RequestProbeAsync(admin, await CreateDeviceAsync(admin, site, gateway));
            await data.ExecuteAsync("UPDATE hub.commands SET retryable = false WHERE id = @job", new { job });
            var first = await central.LeaseAsync(None);
            await data.ExecuteAsync("UPDATE hub.commands SET lease_until = now() - interval '1 second' WHERE id = @job", new { job });

            Assert.Null(await central.LeaseAsync(None));
            Assert.Null(await central.LeaseAsync(None));
            Assert.Equal("unknown", await data.ScalarAsync<string>("SELECT status FROM hub.commands WHERE id = @job", new { job }));
            Assert.Equal(1, await data.ScalarAsync<int>("SELECT attempts FROM hub.commands WHERE id = @job", new { job }));

            // Resposta atrasada do mesmo lease e informacao valida.
            await central.SendResultAsync(job, new EdgeJobResult(first!.FencingToken, EdgeJobOutcomes.Succeeded, null, null), None);
            Assert.Equal("completed", await data.ScalarAsync<string>("SELECT status FROM hub.commands WHERE id = @job", new { job }));
        }
    }

    [Fact]
    public async Task Queued_command_past_its_deadline_is_not_delivered()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var job = await RequestProbeAsync(admin, await CreateDeviceAsync(admin, site, gateway));
            await data.ExecuteAsync("UPDATE hub.commands SET expires_at = now() - interval '1 second' WHERE id = @job", new { job });

            Assert.Null(await central.LeaseAsync(None));
            Assert.Equal("expired", await data.ScalarAsync<string>("SELECT status FROM hub.commands WHERE id = @job", new { job }));
        }
    }

    [Fact]
    public async Task Attempts_are_capped_for_repeatable_commands()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var job = await RequestProbeAsync(admin, await CreateDeviceAsync(admin, site, gateway));
            for (var i = 0; i < 3; i++)
            {
                Assert.NotNull(await central.LeaseAsync(None));
                await data.ExecuteAsync("UPDATE hub.commands SET lease_until = now() - interval '1 second' WHERE id = @job", new { job });
            }

            Assert.Null(await central.LeaseAsync(None));
            Assert.Equal("failed", await data.ScalarAsync<string>("SELECT status FROM hub.commands WHERE id = @job", new { job }));
        }
    }

    // Do cadastro ao diagnostico, com o gateway real e o equipamento simulado

    private async Task<(GatewayAgent Agent, Guid Job, ControlIdSimulator Device, X509Certificate2 DeviceCertificate, HttpClient Admin, Guid DeviceId, CentralClient Central, X509Certificate2 Identity)> PrepareEndToEndAsync(GatewayOptions? overrideOptions = null, bool setCredential = true, string? cidr = "127.0.0.0/8", int? portOverride = null)
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var deviceCertificate = TestCertificates.SelfSigned();
        var simulator = await ControlIdSimulator.StartAsync(deviceCertificate);

        var gateway = await CreateGatewayAsync(admin, site);
        var token = await NewTokenAsync(admin, gateway);
        var deviceId = await CreateDeviceAsync(admin, site, gateway, "127.0.0.1", portOverride ?? simulator.Address.Port, TestCertificates.Pin(deviceCertificate));

        if (setCredential)
        {
            var put = await admin.PutAsJsonAsync(Url($"/api/v1/devices/{deviceId}/credential"), new { login = ControlIdSimulator.ValidLogin, password = ControlIdSimulator.ValidPassword });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        var job = await RequestProbeAsync(admin, deviceId);

        var options = overrideOptions ?? new GatewayOptions
        {
            EnrollmentToken = token,
            ConnectorEnabled = true,
            AllowedCidrs = cidr is null ? [] : [cidr],
        };
        var enrollOptions = new GatewayOptions
        {
            EnrollmentToken = token,
            ConnectorEnabled = options.ConnectorEnabled,
            AllowedCidrs = options.AllowedCidrs,
        };
        var identity = NewIdentity();
        var central = stack.Central(identity);
        var agent = new GatewayAgent(central, new JobExecutor(enrollOptions, NullLoggerFactory.Instance), enrollOptions, NullLogger<GatewayAgent>.Instance);
        return (agent, job, simulator, deviceCertificate, admin, deviceId, central, identity);
    }

    [Fact]
    public async Task Probe_travels_from_operator_to_gateway_to_device_and_back()
    {
        var (agent, job, device, certificate, admin, deviceId, central, identity) = await PrepareEndToEndAsync();
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);

            var result = await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("completed", result.GetProperty("status").GetString());
            var probe = result.GetProperty("result");
            Assert.Equal("Reachable", probe.GetProperty("reachability").GetString());
            Assert.Equal("Verified", probe.GetProperty("tls").GetString());
            Assert.Equal("Valid", probe.GetProperty("authentication").GetString());
            Assert.Equal("SIMULADO-0.0", probe.GetProperty("firmware").GetString());

            var registered = await (await admin.GetAsync(Url($"/api/v1/devices/{deviceId}"))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("SIMULADO-0.0", registered.GetProperty("firmwareVersion").GetString());
            Assert.Equal("reachable", registered.GetProperty("status").GetString());
            Assert.True(registered.GetProperty("hasCredential").GetBoolean());

            Assert.Contains("/logout.fcgi", device.Calls);
        }
    }

    [Fact]
    public async Task Device_password_never_appears_outside_the_encrypted_store()
    {
        var (agent, job, device, certificate, admin, deviceId, central, identity) = await PrepareEndToEndAsync();
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);

            var registered = await (await admin.GetAsync(Url($"/api/v1/devices/{deviceId}"))).Content.ReadAsStringAsync();
            var jobBody = await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadAsStringAsync();
            Assert.DoesNotContain(ControlIdSimulator.ValidPassword, registered, StringComparison.Ordinal);
            Assert.DoesNotContain(ControlIdSimulator.ValidPassword, jobBody, StringComparison.Ordinal);

            // Varre as linhas de cada tabela: nenhuma pode ter a senha, que so existe cifrada no object storage.
            var password = ControlIdSimulator.ValidPassword;
            foreach (var table in new[] { "commands", "devices", "audit_log", "outbox", "idempotency_keys", "blob_objects", "edge_enrollments" })
            {
                var found = await data.ScalarAsync<long>($"SELECT count(*) FROM hub.{table} t WHERE t::text LIKE '%' || @password || '%'", new { password });
                Assert.Equal(0L, found);
            }

            var withBody = await data.ScalarAsync<long>(
                "SELECT count(*) FROM hub.idempotency_keys WHERE position(convert_to(@password, 'UTF8') IN coalesce(response_body, ''::bytea)) > 0", new { password });
            Assert.Equal(0L, withBody);
        }
    }

    [Fact]
    public async Task Probe_without_credential_reports_it_and_never_tries_the_device()
    {
        var (agent, job, device, certificate, admin, _, central, identity) = await PrepareEndToEndAsync(setCredential: false);
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);

            var result = await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("failed", result.GetProperty("status").GetString());
            Assert.Equal("no_credentials", result.GetProperty("failureCode").GetString());
            Assert.Empty(device.Calls);
        }
    }

    [Fact]
    public async Task Address_outside_approved_ranges_is_refused_by_the_gateway()
    {
        var (agent, job, device, certificate, admin, _, central, identity) = await PrepareEndToEndAsync(cidr: "10.0.0.0/8");
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);

            var result = await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("address_not_allowed", result.GetProperty("failureCode").GetString());
            Assert.Empty(device.Calls);
        }
    }

    [Fact]
    public async Task No_approved_range_means_no_device_is_reachable()
    {
        var (agent, job, device, certificate, admin, _, central, identity) = await PrepareEndToEndAsync(cidr: null);
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);

            Assert.Equal("address_not_allowed", (await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failureCode").GetString());
            Assert.Empty(device.Calls);
        }
    }

    [Fact]
    public async Task Connector_stays_off_until_enabled_at_the_gateway()
    {
        var disabled = new GatewayOptions { ConnectorEnabled = false, AllowedCidrs = ["127.0.0.0/8"] };
        var (agent, job, device, certificate, admin, _, central, identity) = await PrepareEndToEndAsync(disabled);
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);

            Assert.Equal("connector_disabled", (await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failureCode").GetString());
            Assert.Empty(device.Calls);
        }
    }

    [Fact]
    public async Task Wrong_device_certificate_is_reported_and_credentials_are_not_sent()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        using var certificate = TestCertificates.SelfSigned();
        await using var simulator = await ControlIdSimulator.StartAsync(certificate);
        var (gateway, central, identity) = await EnrolledGatewayAsync(admin, site);
        using (central)
        using (identity)
        {
            var device = await CreateDeviceAsync(admin, site, gateway, "127.0.0.1", simulator.Address.Port, new string('B', 64));
            await admin.PutAsJsonAsync(Url($"/api/v1/devices/{device}/credential"), new { login = ControlIdSimulator.ValidLogin, password = ControlIdSimulator.ValidPassword });
            var job = await RequestProbeAsync(admin, device);
            var options = new GatewayOptions { ConnectorEnabled = true, AllowedCidrs = ["127.0.0.0/8"] };
            var agent = new GatewayAgent(central, new JobExecutor(options, NullLoggerFactory.Instance), options, NullLogger<GatewayAgent>.Instance);

            await agent.RunCycleAsync(None);

            var probe = (await (await admin.GetAsync(Url($"/api/v1/jobs/{job}"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
            Assert.Equal("Rejected", probe.GetProperty("tls").GetString());
            Assert.Equal("NotTested", probe.GetProperty("authentication").GetString());
            Assert.Empty(simulator.Calls);
        }
    }

    [Fact]
    public async Task Unknown_job_kind_is_refused_by_the_gateway()
    {
        var options = new GatewayOptions { ConnectorEnabled = true, AllowedCidrs = ["0.0.0.0/0"] };
        var executor = new JobExecutor(options, NullLoggerFactory.Instance);

        var result = await executor.ExecuteAsync(new EdgeJob(Guid.NewGuid(), "door.unlock", 1, DateTimeOffset.UtcNow, null, null), None);

        Assert.Equal("kind_not_allowed", result.FailureCode);
        Assert.Equal(EdgeJobOutcomes.Failed, result.Outcome);
    }

    [Fact]
    public async Task Agent_enrolls_itself_on_first_run_and_only_once()
    {
        var (agent, _, device, certificate, admin, _, central, identity) = await PrepareEndToEndAsync(setCredential: false);
        await using (device)
        using (certificate)
        using (central)
        using (identity)
        {
            await agent.RunCycleAsync(None);
            await agent.RunCycleAsync(None);

            var gateways = await (await admin.GetAsync(Url("/api/v1/gateways"))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("active", gateways[0].GetProperty("status").GetString());
        }
    }

    // Cadastro e permissoes

    [Fact]
    public async Task Device_registration_validates_and_never_accepts_other_tenant_or_site_gateway()
    {
        var (_, siteA, adminA) = await TenantWithAdminAsync();
        var (_, siteB, adminB) = await TenantWithAdminAsync();
        var gatewayA = await CreateGatewayAsync(adminA, siteA);
        var gatewayB = await CreateGatewayAsync(adminB, siteB);

        var foreign = await adminA.PostAsJsonAsync(Url("/api/v1/devices"), new { siteId = siteA, gatewayId = gatewayB, name = "x", host = "10.0.0.1", port = 443, tlsPin = new string('A', 64) });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        foreach (var invalid in new object[]
        {
            new { siteId = siteA, gatewayId = gatewayA, name = "x", host = "nao-e-ip", port = 443, tlsPin = new string('A', 64) },
            new { siteId = siteA, gatewayId = gatewayA, name = "x", host = "10.0.0.1", port = 70000, tlsPin = new string('A', 64) },
            new { siteId = siteA, gatewayId = gatewayA, name = "x", host = "10.0.0.1", port = 443, tlsPin = "curto" },
            new { siteId = siteA, gatewayId = gatewayA, name = " ", host = "10.0.0.1", port = 443, tlsPin = new string('A', 64) },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await adminA.PostAsJsonAsync(Url("/api/v1/devices"), invalid)).StatusCode);
        }

        var badMode = await adminA.PostAsJsonAsync(Url("/api/v1/devices"), new { siteId = siteA, gatewayId = gatewayA, name = "x", host = "10.0.0.1", port = 443, tlsMode = "inseguro" });
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task Other_tenant_cannot_read_probe_or_set_credential()
    {
        var (_, siteA, adminA) = await TenantWithAdminAsync();
        var (_, _, adminB) = await TenantWithAdminAsync();
        var device = await CreateDeviceAsync(adminA, siteA, await CreateGatewayAsync(adminA, siteA));
        var job = await RequestProbeAsync(adminA, device);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync(Url($"/api/v1/devices/{device}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync(Url($"/api/v1/jobs/{job}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync(Url($"/api/v1/devices/{device}/probe"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync(Url($"/api/v1/devices/{device}/credential"), new { login = "a", password = "b" })).StatusCode);
    }

    [Fact]
    public async Task Probe_and_credential_need_their_own_permissions()
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        var admin = stack.AdminClient(await data.ActorAsync(tenant, AllPermissions));
        var device = await CreateDeviceAsync(admin, site, await CreateGatewayAsync(admin, site));
        var reader = stack.AdminClient(await data.ActorAsync(tenant, Permissions.DeviceRead));

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(Url($"/api/v1/devices/{device}/probe"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(Url($"/api/v1/devices/{device}/credential"), new { login = "a", password = "b" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Url("/api/v1/gateways"), new { siteId = site, name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Url($"/api/v1/devices/{device}"))).StatusCode);
    }

    [Fact]
    public async Task Site_scoped_operator_only_reaches_devices_of_that_site()
    {
        var tenant = await data.TenantAsync();
        var site1 = await data.SiteAsync(tenant.Id, "Um");
        var site2 = await data.SiteAsync(tenant.Id, "Dois");
        var admin = stack.AdminClient(await data.ActorAsync(tenant, AllPermissions));
        var device1 = await CreateDeviceAsync(admin, site1, await CreateGatewayAsync(admin, site1));
        var device2 = await CreateDeviceAsync(admin, site2, await CreateGatewayAsync(admin, site2));

        var scoped = await data.ActorAsync(tenant);
        await data.GrantAsync(scoped, Permissions.DeviceRead, site1);
        await data.GrantAsync(scoped, Permissions.DeviceProbe, site1);
        var client = stack.AdminClient(scoped);

        var list = await (await client.GetAsync(Url("/api/v1/devices"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(device1, list.EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url($"/api/v1/devices/{device2}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url($"/api/v1/devices/{device2}/probe"), null)).StatusCode);
    }

    [Fact]
    public async Task Setting_a_new_credential_destroys_the_previous_one()
    {
        var (_, site, admin) = await TenantWithAdminAsync();
        var device = await CreateDeviceAsync(admin, site, await CreateGatewayAsync(admin, site));
        var url = Url($"/api/v1/devices/{device}/credential");

        await admin.PutAsJsonAsync(url, new { login = "a", password = "primeira" });
        var first = await data.ScalarAsync<string>("SELECT secret_ref FROM hub.devices WHERE id = @device", new { device });
        await admin.PutAsJsonAsync(url, new { login = "a", password = "segunda" });
        var second = await data.ScalarAsync<string>("SELECT secret_ref FROM hub.devices WHERE id = @device", new { device });

        Assert.NotEqual(first, second);
        Assert.Equal(1L, await data.ScalarAsync<long>(
            "SELECT count(*) FROM hub.blob_objects WHERE id = @first::uuid AND destroyed_at IS NOT NULL AND wrapped_dek IS NULL", new { first }));
        Assert.Equal("device_secret", await data.ScalarAsync<string>("SELECT kind FROM hub.blob_objects WHERE id = @second::uuid", new { second }));
    }

    [Fact]
    public async Task Credential_request_is_not_kept_in_idempotency_records()
    {
        var (admin, site, client) = await TenantWithAdminAsync();
        var device = await CreateDeviceAsync(client, site, await CreateGatewayAsync(client, site));
        var before = await data.ScalarAsync<long>("SELECT count(*) FROM hub.idempotency_keys WHERE actor_id = @Id", new { admin.Id });

        await client.PutAsJsonAsync(Url($"/api/v1/devices/{device}/credential"), new { login = "a", password = "b" });

        Assert.Equal(before, await data.ScalarAsync<long>("SELECT count(*) FROM hub.idempotency_keys WHERE actor_id = @Id", new { admin.Id }));
    }
}
