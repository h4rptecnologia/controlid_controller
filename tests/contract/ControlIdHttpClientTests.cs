using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Platform.Modules.DeviceIntegration.ControlId;

namespace Platform.ContractTests;

internal sealed class CapturingLogger : ILogger<ControlIdHttpClient>
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{formatter(state, exception)} {exception}");
}

public sealed class ControlIdHttpClientTests : IAsyncLifetime
{
    private static readonly ControlIdClientOptions Enabled = new() { Enabled = true };
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = TestCertificates.SelfSigned();
    private readonly CapturingLogger logger = new();
    private ControlIdSimulator simulator = null!;

    public async Task InitializeAsync() => simulator = await ControlIdSimulator.StartAsync(certificate);

    public async Task DisposeAsync()
    {
        await simulator.DisposeAsync();
        certificate.Dispose();
    }

    private static Task<DeviceCredentials> Good(CancellationToken _) =>
        Task.FromResult(new DeviceCredentials(ControlIdSimulator.ValidLogin, ControlIdSimulator.ValidPassword));

    private ControlIdHttpClient Client(ControlIdClientOptions? options = null, DeviceTlsPolicy? tls = null, Func<CancellationToken, Task<DeviceCredentials>>? credentials = null) =>
        new(
            DeviceEndpoint.Https(simulator.Address, tls ?? DeviceTlsPolicy.PinnedCertificate(TestCertificates.Pin(certificate))),
            credentials ?? Good,
            options ?? Enabled,
            logger);

    private static async Task<DeviceFailure> FailureOf(Func<Task> call) =>
        (await Assert.ThrowsAsync<DeviceCallException>(call)).Failure;

    // Contrato da documentacao oficial

    [Fact]
    public async Task Login_follows_the_documented_contract()
    {
        using var client = Client();

        await client.GetSystemInfoAsync(None);

        var login = simulator.Requests.First(r => r.Path == "/login.fcgi");
        Assert.Equal("POST", login.Method);
        Assert.StartsWith("application/json", login.ContentType, StringComparison.Ordinal);
        Assert.Empty(login.Query);
        Assert.Empty(login.Expect);

        using var body = JsonDocument.Parse(login.Body);
        Assert.Equal(ControlIdSimulator.ValidLogin, body.RootElement.GetProperty("login").GetString());
        Assert.Equal(ControlIdSimulator.ValidPassword, body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Session_goes_in_the_query_and_is_reused()
    {
        using var client = Client();

        await client.GetSystemInfoAsync(None);
        await client.GetSystemInfoAsync(None);

        Assert.Equal(1, simulator.LoginCount);
        var calls = simulator.Requests.Where(r => r.Path == "/system_information.fcgi").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal($"?session={ControlIdSimulator.SessionPrefix}1", c.Query));
        Assert.All(calls, c => Assert.DoesNotContain(ControlIdSimulator.SessionPrefix, c.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task System_information_maps_documented_fields_without_precision_loss()
    {
        using var client = Client();

        var info = await client.GetSystemInfoAsync(None);

        Assert.Equal("SIMULADO-0.0", info.Firmware);
        Assert.Equal("SIM-0001", info.Serial);
        Assert.Equal("9007199254740993", info.SourceDeviceId); // Acima de 2^53.
        Assert.True(info.SslEnabled);
        Assert.Equal(443, info.WebServerPort);
        Assert.False(info.Online);
        Assert.True(info.OnlineAvailable);
    }

    [Fact]
    public async Task Numeric_device_id_keeps_every_digit()
    {
        simulator.Overrides["/system_information.fcgi"] = c =>
        {
            c.Response.ContentType = "application/json";
            return c.Response.WriteAsync("""{"device_id":18446744073709551615,"time":1}""");
        };
        using var client = Client();

        Assert.Equal("18446744073709551615", (await client.GetSystemInfoAsync(None)).SourceDeviceId);
    }

    [Fact]
    public async Task Missing_and_unexpected_fields_do_not_break_nor_get_invented()
    {
        simulator.Overrides["/system_information.fcgi"] = c =>
        {
            c.Response.ContentType = "application/json";
            return c.Response.WriteAsync("""{"campo_novo_de_firmware":{"x":1},"version":123456,"network":"nao-objeto"}""");
        };
        using var client = Client();

        var info = await client.GetSystemInfoAsync(None);

        Assert.Equal("123456", info.Firmware);
        Assert.Null(info.Serial);
        Assert.Null(info.DeviceTimeUnix);
        Assert.Null(info.SslEnabled);
    }

    [Fact]
    public async Task Cloud_authentication_code_never_leaves_the_client()
    {
        using var client = Client();

        var probe = await client.ProbeAsync(None);

        Assert.DoesNotContain("CODIGOCLOUDSECRETO", probe.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CODIGOCLOUDSECRETO", JsonSerializer.Serialize(probe), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_sends_the_session_and_clears_it_locally()
    {
        using var client = Client();
        await client.GetSystemInfoAsync(None);

        await client.LogoutAsync(None);

        Assert.Equal(1, simulator.Calls.Count(c => c == "/logout.fcgi"));
        Assert.False(await client.IsSessionValidAsync(None));

        await client.GetSystemInfoAsync(None);
        Assert.Equal(2, simulator.LoginCount);
    }

    [Fact]
    public async Task Logout_clears_locally_even_if_the_device_fails()
    {
        using var client = Client();
        await client.GetSystemInfoAsync(None);
        simulator.Overrides["/logout.fcgi"] = c =>
        {
            c.Abort();
            return Task.CompletedTask;
        };

        await client.LogoutAsync(None);

        Assert.False(await client.IsSessionValidAsync(None));
    }

    [Fact]
    public async Task Session_is_valid_reflects_the_device_answer()
    {
        using var client = Client();
        Assert.False(await client.IsSessionValidAsync(None));

        await client.GetSystemInfoAsync(None);
        Assert.True(await client.IsSessionValidAsync(None));

        simulator.InvalidateSession();
        Assert.False(await client.IsSessionValidAsync(None));
    }

    // TLS: nenhuma credencial sai antes de a identidade ser aceita

    [Fact]
    public async Task System_trust_rejects_self_signed_and_sends_no_credentials()
    {
        using var client = Client(tls: DeviceTlsPolicy.SystemTrust());

        Assert.Equal(DeviceFailure.TlsRejected, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Empty(simulator.Calls);
    }

    [Fact]
    public async Task Wrong_pin_rejects_and_sends_no_credentials()
    {
        using var other = TestCertificates.SelfSigned();
        using var client = Client(tls: DeviceTlsPolicy.PinnedCertificate(TestCertificates.Pin(other)));

        Assert.Equal(DeviceFailure.TlsRejected, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Empty(simulator.Calls);
    }

    [Fact]
    public async Task Pin_accepts_the_exact_certificate_even_with_another_name()
    {
        using var named = TestCertificates.SelfSigned("terminal-sem-relacao-com-o-ip");
        await using var device = await ControlIdSimulator.StartAsync(named);
        using var client = new ControlIdHttpClient(
            DeviceEndpoint.Https(device.Address, DeviceTlsPolicy.PinnedCertificate(TestCertificates.Pin(named))), Good, Enabled, logger);

        Assert.Equal("SIM-0001", (await client.GetSystemInfoAsync(None)).Serial);
    }

    [Fact]
    public async Task Expired_pinned_certificate_is_rejected()
    {
        using var expired = TestCertificates.SelfSigned(validFor: TimeSpan.FromHours(1), startOffset: TimeSpan.FromHours(-2));
        await using var device = await ControlIdSimulator.StartAsync(expired);
        using var client = new ControlIdHttpClient(
            DeviceEndpoint.Https(device.Address, DeviceTlsPolicy.PinnedCertificate(TestCertificates.Pin(expired))), Good, Enabled, logger);

        Assert.Equal(DeviceFailure.TlsRejected, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Empty(device.Calls);
    }

    [Fact]
    public async Task Private_ca_accepts_issued_certificate_and_checks_the_name()
    {
        using var authority = TestCertificates.Authority();
        using var issued = TestCertificates.IssuedBy(authority, "127.0.0.1");
        await using var device = await ControlIdSimulator.StartAsync(issued);
        using var client = new ControlIdHttpClient(DeviceEndpoint.Https(device.Address, DeviceTlsPolicy.PrivateCa(authority)), Good, Enabled, logger);

        Assert.Equal("SIM-0001", (await client.GetSystemInfoAsync(None)).Serial);

        using var wrongName = TestCertificates.IssuedBy(authority, "outro-terminal");
        await using var impostor = await ControlIdSimulator.StartAsync(wrongName);
        using var refused = new ControlIdHttpClient(DeviceEndpoint.Https(impostor.Address, DeviceTlsPolicy.PrivateCa(authority)), Good, Enabled, logger);

        Assert.Equal(DeviceFailure.TlsRejected, await FailureOf(() => refused.GetSystemInfoAsync(None)));
        Assert.Empty(impostor.Calls);
    }

    [Fact]
    public async Task Certificate_from_another_ca_is_rejected()
    {
        using var trusted = TestCertificates.Authority();
        using var other = TestCertificates.Authority();
        using var issued = TestCertificates.IssuedBy(other, "127.0.0.1");
        await using var device = await ControlIdSimulator.StartAsync(issued);
        using var client = new ControlIdHttpClient(DeviceEndpoint.Https(device.Address, DeviceTlsPolicy.PrivateCa(trusted)), Good, Enabled, logger);

        Assert.Equal(DeviceFailure.TlsRejected, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Empty(device.Calls);
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("abcd")]
    public void Malformed_pin_is_refused(string pin)
    {
        Assert.ThrowsAny<FormatException>(() => Throwing(pin));
    }

    private static void Throwing(string pin)
    {
        try
        {
            DeviceTlsPolicy.PinnedCertificate(pin);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    // Ligado so por decisao explicita

    [Fact]
    public void Connector_is_off_by_default()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Client(new ControlIdClientOptions()));
        Assert.Contains("desligado", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_needs_explicit_permission_and_is_reported_as_not_tls()
    {
        await using var device = await ControlIdSimulator.StartAsync(null);
        var endpoint = DeviceEndpoint.PlainHttp(device.Address);

        Assert.Throws<InvalidOperationException>(() => new ControlIdHttpClient(endpoint, Good, Enabled, logger));

        using var allowed = new ControlIdHttpClient(endpoint, Good, new ControlIdClientOptions { Enabled = true, AllowPlainHttp = true }, logger);
        Assert.Equal(ProbeTls.NotUsed, (await allowed.ProbeAsync(None)).Tls);
    }

    [Theory]
    [InlineData("https://user:senha@192.0.2.1/")]
    [InlineData("https://192.0.2.1/caminho")]
    [InlineData("https://192.0.2.1/?session=x")]
    [InlineData("http://192.0.2.1/")]
    public void Invalid_https_address_is_refused(string address)
    {
        Assert.Throws<ArgumentException>(() => DeviceEndpoint.Https(new Uri(address), DeviceTlsPolicy.SystemTrust()));
    }

    // Sessao

    [Fact]
    public async Task Rejected_session_logs_in_again_once_on_read()
    {
        using var client = Client();
        await client.GetSystemInfoAsync(None);
        simulator.InvalidateSession();

        Assert.Equal("SIM-0001", (await client.GetSystemInfoAsync(None)).Serial);
        Assert.Equal(2, simulator.LoginCount);
    }

    [Fact]
    public async Task Session_rejected_twice_stops_without_looping()
    {
        using var client = Client();
        simulator.Overrides["/system_information.fcgi"] = c =>
        {
            c.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        Assert.Equal(DeviceFailure.SessionExpired, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Equal(2, simulator.LoginCount);
        Assert.Equal(2, simulator.Calls.Count(c => c == "/system_information.fcgi"));
    }

    [Fact]
    public async Task Concurrent_calls_share_a_single_login()
    {
        using var client = Client();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => client.GetSystemInfoAsync(None)));

        Assert.Equal(1, simulator.LoginCount);
    }

    [Fact]
    public async Task Concurrent_rejections_share_a_single_new_login()
    {
        using var client = Client();
        await client.GetSystemInfoAsync(None);
        simulator.InvalidateSession();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetSystemInfoAsync(None)));

        Assert.Equal(2, simulator.LoginCount);
    }

    [Fact]
    public async Task Wrong_credentials_fail_without_retrying()
    {
        using var client = Client(credentials: _ => Task.FromResult(new DeviceCredentials("x", "y")));

        Assert.Equal(DeviceFailure.AuthenticationFailed, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Equal(1, simulator.LoginCount);
    }

    [Fact]
    public async Task Credentials_are_fetched_at_each_login_only()
    {
        var fetched = 0;
        using var client = Client(credentials: t =>
        {
            Interlocked.Increment(ref fetched);
            return Good(t);
        });

        await client.GetSystemInfoAsync(None);
        await client.GetSystemInfoAsync(None);
        Assert.Equal(1, fetched);

        simulator.InvalidateSession();
        await client.GetSystemInfoAsync(None);
        Assert.Equal(2, fetched);
    }

    // Falhas

    [Fact]
    public async Task Timeout_is_distinguished_from_cancellation()
    {
        simulator.Delay = TimeSpan.FromSeconds(5);
        using var client = Client(new ControlIdClientOptions { Enabled = true, RequestTimeout = TimeSpan.FromMilliseconds(200) });

        Assert.Equal(DeviceFailure.Timeout, await FailureOf(() => client.GetSystemInfoAsync(None)));

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var patient = Client();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patient.GetSystemInfoAsync(cancel.Token));
    }

    [Fact]
    public async Task Unreachable_device_is_reported()
    {
        var address = simulator.Address;
        await simulator.DisposeAsync();
        using var client = new ControlIdHttpClient(
            DeviceEndpoint.Https(address, DeviceTlsPolicy.PinnedCertificate(TestCertificates.Pin(certificate))), Good, Enabled, logger);

        Assert.Equal(DeviceFailure.Unreachable, await FailureOf(() => client.GetSystemInfoAsync(None)));
    }

    [Fact]
    public async Task Redirect_is_not_followed_so_credentials_never_reach_another_host()
    {
        await using var attacker = await ControlIdSimulator.StartAsync(certificate);
        simulator.Overrides["/login.fcgi"] = c =>
        {
            c.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            c.Response.Headers.Location = new Uri(attacker.Address, "login.fcgi").ToString();
            return Task.CompletedTask;
        };
        using var client = Client();

        Assert.Equal(DeviceFailure.InvalidResponse, await FailureOf(() => client.GetSystemInfoAsync(None)));
        Assert.Empty(attacker.Calls);
    }

    [Theory]
    [InlineData("nao e json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("""{"session":""}""")]
    public async Task Malformed_login_answer_is_invalid_response(string body)
    {
        simulator.Overrides["/login.fcgi"] = c => c.Response.WriteAsync(body);
        using var client = Client();

        Assert.Equal(DeviceFailure.InvalidResponse, await FailureOf(() => client.GetSystemInfoAsync(None)));
    }

    [Fact]
    public async Task Oversized_response_is_refused()
    {
        simulator.Overrides["/system_information.fcgi"] = c => c.Response.WriteAsync("{\"x\":\"" + new string('a', 4096) + "\"}");
        using var client = Client(new ControlIdClientOptions { Enabled = true, MaxResponseBytes = 1024 });

        Assert.Equal(DeviceFailure.InvalidResponse, await FailureOf(() => client.GetSystemInfoAsync(None)));
    }

    [Fact]
    public async Task Server_error_is_reported_with_status_only()
    {
        simulator.Overrides["/system_information.fcgi"] = c =>
        {
            c.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return c.Response.WriteAsync("detalhe-interno-marcador");
        };
        using var client = Client();

        var error = await Assert.ThrowsAsync<DeviceCallException>(() => client.GetSystemInfoAsync(None));

        Assert.Equal(DeviceFailure.DeviceError, error.Failure);
        Assert.Equal(500, error.HttpStatus);
        Assert.DoesNotContain("detalhe-interno-marcador", error.ToString(), StringComparison.Ordinal);
    }

    // Diagnostico por etapa (RF-004)

    [Fact]
    public async Task Probe_reports_each_step_and_clock_skew()
    {
        simulator.DeviceTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;
        using var client = Client();

        var probe = await client.ProbeAsync(None);

        Assert.Equal(ProbeReachability.Reachable, probe.Reachability);
        Assert.Equal(ProbeTls.Verified, probe.Tls);
        Assert.Equal(ProbeAuthentication.Valid, probe.Authentication);
        Assert.Null(probe.Failure);
        Assert.InRange(probe.ClockSkewSeconds!.Value, -310, -290);
    }

    [Fact]
    public async Task Probe_separates_tls_failure_and_does_not_try_credentials()
    {
        var fetched = false;
        using var client = Client(tls: DeviceTlsPolicy.SystemTrust(), credentials: t =>
        {
            fetched = true;
            return Good(t);
        });

        var probe = await client.ProbeAsync(None);

        Assert.Equal(ProbeTls.Rejected, probe.Tls);
        Assert.Equal(ProbeAuthentication.NotTested, probe.Authentication);
        Assert.False(fetched);
        Assert.Null(probe.System);
    }

    [Fact]
    public async Task Probe_separates_wrong_credentials()
    {
        using var client = Client(credentials: _ => Task.FromResult(new DeviceCredentials("x", "y")));

        var probe = await client.ProbeAsync(None);

        Assert.Equal(ProbeReachability.Reachable, probe.Reachability);
        Assert.Equal(ProbeTls.Verified, probe.Tls);
        Assert.Equal(ProbeAuthentication.Rejected, probe.Authentication);
    }

    [Fact]
    public async Task Probe_never_declares_a_verified_capability()
    {
        Assert.DoesNotContain(typeof(DeviceProbeResult).GetProperties(), p => p.Name.Contains("Capab", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    // Segredos

    [Fact]
    public async Task Credentials_do_not_print_the_password()
    {
        var credential = await Good(None);
        Assert.DoesNotContain(ControlIdSimulator.ValidPassword, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ControlIdSimulator.ValidPassword, JsonSerializer.Serialize(credential), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_failure_path_exposes_session_or_password_in_logs_or_exceptions()
    {
        var seen = new List<string>();
        using var client = Client();
        await client.GetSystemInfoAsync(None);

        // Sessao recusada duas vezes, erro do equipamento, corpo invalido e queda de conexao.
        foreach (var failing in new Func<HttpContext, Task>[]
        {
            c => { c.Response.StatusCode = 401; return Task.CompletedTask; },
            c => { c.Response.StatusCode = 500; return Task.CompletedTask; },
            c => c.Response.WriteAsync("nao e json"),
            c => { c.Abort(); return Task.CompletedTask; },
        })
        {
            simulator.Overrides["/system_information.fcgi"] = failing;
            var error = await Assert.ThrowsAsync<DeviceCallException>(() => client.GetSystemInfoAsync(None));
            seen.Add(error.ToString());
        }

        simulator.Overrides["/logout.fcgi"] = c => { c.Abort(); return Task.CompletedTask; };
        await client.LogoutAsync(None);
        seen.AddRange(logger.Lines);

        Assert.NotEmpty(logger.Lines);
        Assert.All(seen, text =>
        {
            Assert.DoesNotContain(ControlIdSimulator.SessionPrefix, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ControlIdSimulator.ValidPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain("session=", text, StringComparison.Ordinal);
        });
    }
}
