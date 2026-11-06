using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

public sealed class CentralCallException : Exception
{
    public CentralCallException()
        : base("Falha na comunicacao com a central.")
    {
    }

    public CentralCallException(string message)
        : base(message)
    {
    }

    public CentralCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CentralCallException(HttpStatusCode status)
        : base($"A central respondeu {(int)status}.")
    {
        Status = status;
    }

    public HttpStatusCode? Status { get; }
}

public interface ICentralClient
{
    Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken);

    Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken);

    Task<EdgeJob?> LeaseAsync(CancellationToken cancellationToken);

    Task SendResultAsync(Guid jobId, EdgeJobResult result, CancellationToken cancellationToken);

    Task<IReadOnlyList<EdgeDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    Task<EdgeEventBatchResult> SendEventsAsync(EdgeEventBatch batch, CancellationToken cancellationToken);

    Task SaveCheckpointAsync(Guid jobId, EdgeCheckpointRequest request, CancellationToken cancellationToken);

    Task<EdgeEpochResetResponse> ResetEpochAsync(Guid jobId, EdgeEpochResetRequest request, CancellationToken cancellationToken);
}

/// <summary>Conexao sempre de saida, com mTLS. A central e validada pela cadeia; nao ha modo sem validacao.</summary>
public sealed class CentralClient : ICentralClient, IDisposable
{
    private readonly HttpClient http;

    /// <param name="centralRoot">CA privada da central. Nulo usa a confianca do sistema.</param>
    public CentralClient(Uri centralAddress, X509Certificate2 identity, X509Certificate2? centralRoot, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        handler.SslOptions.ClientCertificates = [identity];

        if (centralRoot is not null)
        {
            var chain = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            chain.CustomTrustStore.Add(centralRoot);
            handler.SslOptions.CertificateChainPolicy = chain;
        }

        http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = centralAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsJsonAsync(Url("edge/v1/enroll"), request, cancellationToken));
        return await ReadAsync<EnrollResponse>(response, cancellationToken);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsJsonAsync(Url("edge/v1/heartbeat"), request, cancellationToken));
        return await ReadAsync<HeartbeatResponse>(response, cancellationToken);
    }

    public async Task<EdgeJob?> LeaseAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsync(Url("edge/v1/jobs/lease"), null, cancellationToken));
        return (await ReadAsync<EdgeJobLease>(response, cancellationToken)).Job;
    }

    public async Task SendResultAsync(Guid jobId, EdgeJobResult result, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsJsonAsync(Url($"edge/v1/jobs/{jobId}/result"), result, cancellationToken));
        EnsureSuccess(response);
    }

    public async Task<IReadOnlyList<EdgeDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.GetAsync(Url("edge/v1/devices"), cancellationToken));
        return (await ReadAsync<EdgeDeviceList>(response, cancellationToken)).Devices;
    }

    public async Task<EdgeEventBatchResult> SendEventsAsync(EdgeEventBatch batch, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsJsonAsync(Url("edge/v1/events"), batch, cancellationToken));
        return await ReadAsync<EdgeEventBatchResult>(response, cancellationToken);
    }

    public async Task SaveCheckpointAsync(Guid jobId, EdgeCheckpointRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsJsonAsync(Url($"edge/v1/jobs/{jobId}/checkpoint"), request, cancellationToken));
        EnsureSuccess(response);
    }

    public async Task<EdgeEpochResetResponse> ResetEpochAsync(Guid jobId, EdgeEpochResetRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => http.PostAsJsonAsync(Url($"edge/v1/jobs/{jobId}/epoch-reset"), request, cancellationToken));
        return await ReadAsync<EdgeEpochResetResponse>(response, cancellationToken);
    }

    public void Dispose() => http.Dispose();

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            // Sem o texto original: a mensagem nao deve levar enderecos para logs de cliente.
            throw new CentralCallException($"Falha de conexao com a central: {ex.HttpRequestError}.");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new CentralCallException();
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new CentralCallException(response.StatusCode);
        }
    }
}
