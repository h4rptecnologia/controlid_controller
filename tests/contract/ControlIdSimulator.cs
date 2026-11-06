using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Platform.ContractTests;

/// <summary>
/// SIMULADOR, nao evidencia de hardware. O formato segue a documentacao oficial; o comportamento em erro e sessao
/// expirada e suposicao nossa, porque o fabricante nao documenta (docs/capability-matrix/contratos-fabricante.md).
/// Servidor real em socket real, para exercitar TLS de verdade.
/// </summary>
public sealed class ControlIdSimulator : IAsyncDisposable
{
    public const string ValidLogin = "operador-lab";
    public const string ValidPassword = "senha-de-laboratorio-marcador";
    public const string SessionPrefix = "SESSAOSECRETA";

    private readonly WebApplication app;
    private int issued;

    private ControlIdSimulator(WebApplication app) => this.app = app;

    public ConcurrentQueue<string> Calls { get; } = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public int LoginCount => Calls.Count(c => c == "/login.fcgi");

    public string? ValidSession { get; set; }

    public TimeSpan Delay { get; set; }

    /// <summary>Substitui a resposta de uma rota, para simular falhas.</summary>
    public ConcurrentDictionary<string, Func<HttpContext, Task>> Overrides { get; } = new();

    public long DeviceTimeUnix { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Tabelas simuladas: nome do objeto para linhas em JSON cru, preservando numeros.</summary>
    public ConcurrentDictionary<string, List<string>> Tables { get; } = new();

    /// <summary>Formato de where que este "firmware" entende; o outro e ignorado, como poderia ocorrer no real.</summary>
    public string UnderstoodWhere { get; set; } = "map";

    public bool IgnoreFields { get; set; }

    public bool IgnoreLimit { get; set; }

    public bool IgnoreOffset { get; set; }

    public Action? BeforeEachPage { get; set; }

    public Uri Address => new(app.Urls.First().Replace("[::1]", "127.0.0.1", StringComparison.Ordinal).Replace("localhost", "127.0.0.1", StringComparison.Ordinal));

    public static async Task<ControlIdSimulator> StartAsync(X509Certificate2? certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
            }));

        var app = builder.Build();
        var simulator = new ControlIdSimulator(app);
        app.Run(simulator.HandleAsync);
        await app.StartAsync();
        return simulator;
    }

    public void InvalidateSession() => ValidSession = null;

    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value!;
        Calls.Enqueue(path);

        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        Requests.Enqueue(new RecordedRequest(
            context.Request.Method,
            path,
            context.Request.QueryString.Value ?? string.Empty,
            context.Request.ContentType,
            context.Request.Headers.Expect.ToString(),
            body));

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, context.RequestAborted);
        }

        if (Overrides.TryGetValue(path, out var replacement))
        {
            await replacement(context);
            return;
        }

        switch (path)
        {
            case "/login.fcgi":
                await LoginAsync(context, body);
                break;
            case "/session_is_valid.fcgi":
                // Doc: unico comando que dispensa sessao. Exemplo oficial: {"session_is_valid": true}
                await context.Response.WriteAsJsonAsync(new { session_is_valid = HasValidSession(context) });
                break;
            case "/logout.fcgi":
                if (Authorized(context))
                {
                    ValidSession = null;
                }

                break;
            case "/load_objects.fcgi":
                if (Authorized(context))
                {
                    await LoadObjectsAsync(context, body);
                }

                break;
            case "/system_information.fcgi":
                if (Authorized(context))
                {
                    // Campos da doc oficial, que nao traz exemplo de resposta. Inclui iDCloud_code de proposito.
                    await context.Response.WriteAsJsonAsync(new
                    {
                        uptime = new { days = 1, hours = 2, minutes = 3, seconds = 4 },
                        time = DeviceTimeUnix,
                        memory = new { disk = new { free = 1, total = 2 }, ram = new { free = 1, total = 2 } },
                        license = new { users = 10, device = 1, type = 0 },
                        biometrics = new { max_num_records = 10 },
                        network = new { mac = "00:00:00:00:00:00", ip = "192.0.2.10", netmask = "255.255.255.0", gateway = "192.0.2.1", web_server_port = 443, ssl_enabled = true, dhcp_enabled = false, ten_mbps = false, dns_primary = "192.0.2.1", dns_secondary = "192.0.2.2" },
                        serial = "SIM-0001",
                        version = "SIMULADO-0.0",
                        device_id = "9007199254740993",
                        secbox_version = "0",
                        iDCloud_code = "CODIGOCLOUDSECRETO",
                        online = false,
                        online_available = true,
                    });
                }

                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private async Task LoadObjectsAsync(HttpContext context, string body)
    {
        BeforeEachPage?.Invoke();

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var name = root.GetProperty("object").GetString()!;
        var rows = Tables.GetValueOrDefault(name, []).Select(r => JsonDocument.Parse(r).RootElement).ToList();

        if (root.TryGetProperty("where", out var where))
        {
            var minimum = MinimumFrom(where, name);
            if (minimum is { } min)
            {
                rows = rows.Where(r => r.GetProperty("id").GetInt64() > min).ToList();
            }
        }

        if (root.TryGetProperty("order", out var order) && order.EnumerateArray().Any(o => o.GetString() == "id"))
        {
            rows = rows.OrderBy(r => r.GetProperty("id").GetInt64()).ToList();
        }

        if (root.TryGetProperty("limit", out var limit) && !IgnoreLimit)
        {
            var offset = root.TryGetProperty("offset", out var skip) && !IgnoreOffset ? skip.GetInt32() : 0;
            rows = rows.Skip(offset).Take(limit.GetInt32()).ToList();
        }

        var requested = root.TryGetProperty("fields", out var fields) && !IgnoreFields
            ? fields.EnumerateArray().Select(f => f.GetString()!).ToHashSet()
            : null;

        var projected = rows.Select(row =>
            "{" + string.Join(',', row.EnumerateObject()
                .Where(p => requested is null || requested.Contains(p.Name))
                .Select(p => $"{JsonSerializer.Serialize(p.Name)}:{p.Value.GetRawText()}")) + "}");

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{{JsonSerializer.Serialize(name)}:[{string.Join(',', projected)}]}}");
    }

    private long? MinimumFrom(JsonElement where, string name)
    {
        if (where.ValueKind == JsonValueKind.Object && UnderstoodWhere == "map")
        {
            return where.GetProperty(name).GetProperty("id").GetProperty(">").GetInt64();
        }

        if (where.ValueKind == JsonValueKind.Array && UnderstoodWhere == "list")
        {
            var first = where[0];
            return first.GetProperty("field").GetString() == "id" && first.GetProperty("operator").GetString() == ">"
                ? first.GetProperty("value").GetInt64()
                : null;
        }

        return null;
    }

    private async Task LoginAsync(HttpContext context, string body)
    {
        using var json = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var ok = json.RootElement.TryGetProperty("login", out var login) && login.GetString() == ValidLogin
            && json.RootElement.TryGetProperty("password", out var password) && password.GetString() == ValidPassword;

        if (!ok)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ValidSession = $"{SessionPrefix}{Interlocked.Increment(ref issued)}";
        await context.Response.WriteAsJsonAsync(new { session = ValidSession });
    }

    private bool HasValidSession(HttpContext context) =>
        ValidSession is not null && context.Request.Query["session"] == ValidSession;

    private bool Authorized(HttpContext context)
    {
        if (HasValidSession(context))
        {
            return true;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return false;
    }
}

public sealed record RecordedRequest(string Method, string Path, string Query, string? ContentType, string Expect, string Body);

public static class TestCertificates
{
    public static X509Certificate2 SelfSigned(string name = "127.0.0.1", TimeSpan? validFor = null, TimeSpan? startOffset = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(San(name));
        return Finish(request, validFor, startOffset);
    }

    public static X509Certificate2 Authority()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=CA Privada de Teste", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return Finish(request, TimeSpan.FromDays(1), null);
    }

    public static X509Certificate2 IssuedBy(X509Certificate2 authority, string name)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(San(name));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var issued = request.Create(authority, now.AddMinutes(-5), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public static string Pin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    private static X509Extension San(string name)
    {
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(name, out var ip))
        {
            san.AddIpAddress(ip);
        }
        else
        {
            san.AddDnsName(name);
        }

        return san.Build();
    }

    private static X509Certificate2 Finish(CertificateRequest request, TimeSpan? validFor, TimeSpan? startOffset)
    {
        var start = DateTimeOffset.UtcNow.Add(startOffset ?? TimeSpan.FromMinutes(-5));
        using var created = request.CreateSelfSigned(start, start.Add(validFor ?? TimeSpan.FromHours(1)));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}
