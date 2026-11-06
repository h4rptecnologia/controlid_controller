using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Platform.Modules.DeviceIntegration.ControlId;

public sealed class DeviceEndpoint
{
    private DeviceEndpoint(Uri baseAddress, DeviceTlsPolicy? tls)
    {
        BaseAddress = baseAddress;
        Tls = tls;
    }

    internal Uri BaseAddress { get; }

    internal DeviceTlsPolicy? Tls { get; }

    internal bool IsPlainHttp => BaseAddress.Scheme == Uri.UriSchemeHttp;

    public static DeviceEndpoint Https(Uri address, DeviceTlsPolicy tls)
    {
        ArgumentNullException.ThrowIfNull(tls);
        Validate(address, Uri.UriSchemeHttps);
        return new DeviceEndpoint(address, tls);
    }

    /// <summary>
    /// HTTP puro, para modelo sem HTTPS confiavel. So funciona com ControlIdClientOptions.AllowPlainHttp e dentro
    /// de segmento isolado: fica classificado como risco aberto (docs/06-seguranca.md).
    /// </summary>
    public static DeviceEndpoint PlainHttp(Uri address)
    {
        Validate(address, Uri.UriSchemeHttp);
        return new DeviceEndpoint(address, null);
    }

    private static void Validate(Uri address, string scheme)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.Scheme != scheme || address.UserInfo.Length > 0
            || address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0)
        {
            throw new ArgumentException("Endereco do equipamento invalido.", nameof(address));
        }
    }
}

public sealed class ControlIdClientOptions
{
    /// <summary>Conector desligado por padrao ate haver homologacao (docs/13-validacoes.md).</summary>
    public bool Enabled { get; init; }

    public bool AllowPlainHttp { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public long MaxResponseBytes { get; init; } = 16 * 1024 * 1024;

    public WhereDialect WhereDialect { get; init; } = WhereDialect.ObjectMap;

    /// <summary>Total de tentativas de uma leitura em falha transitoria. Nao se aplica a mutacoes.</summary>
    public int MaxReadAttempts { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Cliente de um equipamento, com sessao propria guardada so em memoria. Contratos conforme
/// https://www.controlid.com.br/docs/access-api-pt/ e docs/capability-matrix/contratos-fabricante.md.
/// Nao homologado em hardware: a expiracao de sessao e o formato de erro nao sao documentados pelo fabricante.
/// </summary>
public sealed class ControlIdHttpClient : IDeviceConnector, IDisposable
{
    private const string LoginRoute = "login.fcgi";
    private const string LogoutRoute = "logout.fcgi";
    private const string SessionIsValidRoute = "session_is_valid.fcgi";
    private const string SystemInformationRoute = "system_information.fcgi";
    private const string LoadObjectsRoute = "load_objects.fcgi";
    private const int MaxPageSize = 1000;

    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly DeviceEndpoint endpoint;
    private readonly Func<CancellationToken, Task<DeviceCredentials>> credentials;
    private readonly ControlIdClientOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger logger;
    private readonly HttpClient http;
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private string? session;

    /// <param name="credentials">Buscada no cofre a cada login; este objeto nao retem a senha.</param>
    public ControlIdHttpClient(
        DeviceEndpoint endpoint,
        Func<CancellationToken, Task<DeviceCredentials>> credentials,
        ControlIdClientOptions options,
        ILogger<ControlIdHttpClient> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            throw new InvalidOperationException("Conector Control iD desligado por configuracao.");
        }

        if (endpoint.IsPlainHttp && !options.AllowPlainHttp)
        {
            throw new InvalidOperationException("HTTP puro exige autorizacao explicita por configuracao.");
        }

        this.endpoint = endpoint;
        this.credentials = credentials;
        this.options = options;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;

        // Handler proprio porque o TLS e por equipamento. Sem IHttpClientFactory: o log dele traria a URL com a sessao.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // Um redirecionamento levaria a sessao e a senha para outro host.
            UseProxy = false, // Proxies registram URLs, e a sessao esta na URL.
            UseCookies = false,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        endpoint.Tls?.Apply(handler.SslOptions);

        http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpoint.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan, // Prazo controlado por chamada, para distinguir de cancelamento.
            MaxResponseContentBufferSize = options.MaxResponseBytes,
        };
        http.DefaultRequestHeaders.ExpectContinue = false; // Recomendado pela documentacao do fabricante.
    }

    public async Task<DeviceSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAuthenticatedAsync(SystemInformationRoute, null, cancellationToken);
        var root = document.RootElement;
        var network = root.TryGetProperty("network", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

        return new DeviceSystemInfo(
            Text(root, "version"),
            Text(root, "serial"),
            Text(root, "device_id"),
            Integer(root, "time"),
            Flag(network, "ssl_enabled"),
            (int?)Integer(network, "web_server_port"),
            Flag(root, "online"),
            Flag(root, "online_available"));
    }

    public async Task<bool> IsSessionValidAsync(CancellationToken cancellationToken)
    {
        var current = session;
        if (current is null)
        {
            return false;
        }

        using var response = await SendAsync(WithSession(SessionIsValidRoute, current), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        using var document = await ReadJsonAsync(response, cancellationToken);
        return document.RootElement.TryGetProperty("session_is_valid", out var valid) && valid.ValueKind == JsonValueKind.True;
    }

    /// <summary>Limpa a sessao local mesmo se o equipamento nao responder.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var current = Interlocked.Exchange(ref session, null);
        if (current is null)
        {
            return;
        }

        try
        {
            using var response = await SendAsync(WithSession(LogoutRoute, current), null, cancellationToken);
        }
        catch (DeviceCallException ex)
        {
            logger.LogWarning("Logout sem confirmacao do equipamento: {Failure}.", ex.Failure);
        }
    }

    public async Task<DeviceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var reachability = ProbeReachability.Unknown;
        var tls = ProbeTls.Unknown;
        var authentication = ProbeAuthentication.NotTested;

        try
        {
            // Chamada sem sessao: prova alcance e TLS antes de qualquer credencial sair daqui.
            using (await SendAsync(SessionIsValidRoute, null, cancellationToken))
            {
                reachability = ProbeReachability.Reachable;
                tls = endpoint.IsPlainHttp ? ProbeTls.NotUsed : ProbeTls.Verified;
            }

            await EnsureSessionAsync(null, cancellationToken);
            authentication = ProbeAuthentication.Valid;

            var system = await GetSystemInfoAsync(cancellationToken);
            var now = clock.GetUtcNow();
            long? skew = system.DeviceTimeUnix is { } time ? time - now.ToUnixTimeSeconds() : null;

            return new DeviceProbeResult(reachability, tls, authentication, system, skew, null, now);
        }
        catch (DeviceCallException ex)
        {
            switch (ex.Failure)
            {
                case DeviceFailure.TlsRejected:
                    reachability = ProbeReachability.Reachable;
                    tls = ProbeTls.Rejected;
                    break;
                case DeviceFailure.Unreachable:
                    reachability = ProbeReachability.Unreachable;
                    break;
                case DeviceFailure.AuthenticationFailed:
                    authentication = ProbeAuthentication.Rejected;
                    break;
            }

            return new DeviceProbeResult(reachability, tls, authentication, null, null, ex.Failure, clock.GetUtcNow());
        }
    }

    public void Dispose()
    {
        http.Dispose();
        loginGate.Dispose();
    }

    public async Task<DeviceObjectPage> LoadObjectsPageAsync(LoadObjectsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = Validate(request);
        var mode = definition.Key is ["id"] ? PagingMode.Keyset : PagingMode.OffsetBestEffort;
        var body = DeviceObjectRequestBuilder.Build(definition, request, options.WhereDialect, mode);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var document = await SendAuthenticatedAsync(LoadObjectsRoute, body, cancellationToken);
                return DeviceObjectResponseParser.Parse(document, definition, request, mode);
            }
            catch (DeviceCallException ex) when (attempt < options.MaxReadAttempts && IsTransient(ex))
            {
                await Task.Delay(BackoffFor(attempt), clock, cancellationToken);
            }
        }
    }

    public async IAsyncEnumerable<DeviceObjectPage> ReadAllObjectsAsync(LoadObjectsRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = request;
        string? previousFirstKey = null;

        while (true)
        {
            var page = await LoadObjectsPageAsync(current, cancellationToken);

            // Offset ignorado devolve sempre a mesma pagina; sem esta parada a leitura nao terminaria.
            if (page.Paging == PagingMode.OffsetBestEffort && page.Rows.Count > 0)
            {
                var first = page.Rows[0].SourceKey;
                if (first == previousFirstKey)
                {
                    throw new DeviceCallException(DeviceFailure.UnexpectedBehavior);
                }

                previousFirstKey = first;
            }

            yield return page;

            if (!page.HasMore)
            {
                yield break;
            }

            current = current with { Cursor = page.NextCursor };
        }
    }

    private static DeviceObjectDefinition Validate(LoadObjectsRequest request)
    {
        // Lista fechada: nenhum nome de objeto ou campo vem de fora sem estar no catalogo.
        if (!ObjectCatalog.Objects.TryGetValue(request.ObjectName, out var definition))
        {
            throw new ArgumentException("Objeto fora do catalogo.", nameof(request));
        }

        if (definition.Key.Count == 0)
        {
            throw new ArgumentException("Objeto sem chave conhecida no catalogo.", nameof(request));
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Tamanho de pagina invalido.");
        }

        if (request.IncludeSensitive?.Any(field => !definition.Sensitive.Contains(field)) == true)
        {
            throw new ArgumentException("Campo nao pode ser solicitado.", nameof(request));
        }

        if (request.Cursor?.LastId is { } last)
        {
            DeviceObjectResponseParser.ParseId(last);
        }

        return definition;
    }

    private static bool IsTransient(DeviceCallException ex) =>
        ex.Failure is DeviceFailure.Unreachable or DeviceFailure.Timeout
        || (ex.Failure == DeviceFailure.DeviceError && ex.HttpStatus >= 500);

    // Exponencial com jitter, para varios gateways nao voltarem ao mesmo tempo depois de uma queda.
    private TimeSpan BackoffFor(int attempt)
    {
        var ceiling = options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds((ceiling / 2) + RandomNumberGenerator.GetInt32(0, Math.Max(1, (int)(ceiling / 2))));
    }

    private static ByteArrayContent? JsonBody(byte[]? body)
    {
        if (body is null)
        {
            return null;
        }

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = Json;
        return content;
    }

    /// <summary>Leitura: em sessao recusada refaz o login uma vez. Mutacoes nao usam este caminho.</summary>
    private async Task<JsonDocument> SendAuthenticatedAsync(string route, byte[]? body, CancellationToken cancellationToken)
    {
        var current = await EnsureSessionAsync(null, cancellationToken);
        using (var content = JsonBody(body))
        using (var response = await SendAsync(WithSession(route, current), content, cancellationToken))
        {
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return await ReadJsonAsync(response, cancellationToken);
            }
        }

        current = await EnsureSessionAsync(current, cancellationToken);
        using var retriedContent = JsonBody(body);
        using var retried = await SendAsync(WithSession(route, current), retriedContent, cancellationToken);
        if (retried.StatusCode == HttpStatusCode.Unauthorized)
        {
            Interlocked.CompareExchange(ref session, null, current);
            throw new DeviceCallException(DeviceFailure.SessionExpired, (int)retried.StatusCode);
        }

        return await ReadJsonAsync(retried, cancellationToken);
    }

    /// <param name="rejected">Sessao que o equipamento recusou. Quem chegou depois reaproveita o novo login.</param>
    private async Task<string> EnsureSessionAsync(string? rejected, CancellationToken cancellationToken)
    {
        await loginGate.WaitAsync(cancellationToken);
        try
        {
            if (session is { } current && current != rejected)
            {
                return current;
            }

            session = null;
            session = await LoginAsync(cancellationToken);
            return session;
        }
        finally
        {
            loginGate.Release();
        }
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        var credential = await credentials(cancellationToken);
        using var content = JsonContent.Create(new { login = credential.Login, password = credential.Password }, Json);
        await content.LoadIntoBufferAsync(cancellationToken);

        using var response = await SendAsync(LoginRoute, content, cancellationToken);

        // O fabricante nao documenta o erro de login; 4xx e tratado como credencial recusada (a homologar).
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            logger.LogWarning("Login recusado pelo equipamento.");
            throw new DeviceCallException(DeviceFailure.AuthenticationFailed, (int)response.StatusCode);
        }

        using var document = await ReadJsonAsync(response, cancellationToken);
        return document.RootElement.TryGetProperty("session", out var value) && value.GetString() is { Length: > 0 } code
            ? code
            : throw new DeviceCallException(DeviceFailure.InvalidResponse);
    }

    private static string WithSession(string route, string current) =>
        $"{route}?session={Uri.EscapeDataString(current)}";

    // A documentacao mostra POST com Content-Type JSON mesmo sem parametros.
    private async Task<HttpResponseMessage> SendAsync(string relativeUrl, HttpContent? content, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativeUrl, UriKind.Relative));
        using var empty = new ByteArrayContent([]);
        empty.Headers.ContentType = Json;
        request.Content = content ?? empty;

        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new DeviceCallException(DeviceFailure.InvalidResponse, (int)response.StatusCode);
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceCallException(DeviceFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            throw new DeviceCallException(Classify(ex));
        }
    }

    private static DeviceFailure Classify(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.SecureConnectionError => DeviceFailure.TlsRejected,
        HttpRequestError.ConfigurationLimitExceeded => DeviceFailure.InvalidResponse,
        HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => DeviceFailure.InvalidResponse,
        _ => DeviceFailure.Unreachable,
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new DeviceCallException(DeviceFailure.DeviceError, (int)response.StatusCode);
        }

        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
            // O motivo do parser pode citar trechos da resposta; a excecao abaixo nao carrega nada disso.
        }

        throw new DeviceCallException(DeviceFailure.InvalidResponse);
    }

    // Tolerante a string ou numero: a documentacao declara device_id como string, e o blueprint como numerico.
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
