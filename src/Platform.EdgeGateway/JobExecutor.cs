using System.Net;
using Microsoft.Extensions.Logging;
using Platform.Modules.DeviceIntegration.ControlId;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

public sealed class GatewayOptions
{
    public Uri? CentralUrl { get; init; }

    public string IdentityPath { get; init; } = "gateway-identity.pfx";

    public string? EnrollmentToken { get; init; }

    public string? CentralCaPath { get; init; }

    /// <summary>Faixas de rede que este gateway pode acessar. Vazio recusa tudo.</summary>
    public IReadOnlyList<string> AllowedCidrs { get; init; } = [];

    /// <summary>Desligado ate haver homologacao do equipamento (docs/13-validacoes.md).</summary>
    public bool ConnectorEnabled { get; init; }

    public bool AllowPlainHttp { get; init; }

    public string? PrivateCaPath { get; init; }

    /// <summary>Recepcao de eventos do terminal. Desligada ate haver homologacao.</summary>
    public bool MonitorEnabled { get; init; }

    public int MonitorPort { get; init; } = 8000;

    public string MonitorPathPrefix { get; init; } = "api/notifications";

    public string SpoolPath { get; init; } = "spool";

    public int SpoolMaxEvents { get; init; } = 100_000;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Executa o que a central pede, dentro de limites decididos aqui. Mesmo com a central comprometida, o gateway so
/// roda tipos conhecidos e so fala com enderecos das faixas aprovadas localmente.
/// </summary>
public sealed class JobExecutor(GatewayOptions options, ILoggerFactory loggers, Func<DeviceTlsPolicy?>? privateCa = null, ICentralClient? central = null)
{
    private readonly IReadOnlyList<IPNetwork> allowed = options.AllowedCidrs.Select(IPNetwork.Parse).ToList();

    public async Task<EdgeJobResult> ExecuteAsync(EdgeJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.Kind is not (EdgeJobKinds.DeviceProbe or EdgeJobKinds.DeviceBackfill))
        {
            return Failed(job, "kind_not_allowed");
        }

        if (!options.ConnectorEnabled)
        {
            return Failed(job, "connector_disabled");
        }

        if (job.Device is not { } device || !IPAddress.TryParse(device.Host, out var address) || device.Port is < 1 or > 65535)
        {
            return Failed(job, "invalid_target");
        }

        if (!allowed.Any(network => network.Contains(address)))
        {
            return Failed(job, "address_not_allowed");
        }

        if (device.Login is not { } login)
        {
            return Failed(job, "no_credentials");
        }

        var endpoint = BuildEndpoint(device, address);
        if (endpoint is null)
        {
            return Failed(job, "tls_not_configured");
        }

        using var client = new ControlIdHttpClient(
            endpoint,
            _ => Task.FromResult(new DeviceCredentials(login.Login, login.Password)),
            new ControlIdClientOptions { Enabled = true, AllowPlainHttp = options.AllowPlainHttp },
            loggers.CreateLogger<ControlIdHttpClient>());

        try
        {
            if (job.Kind == EdgeJobKinds.DeviceBackfill)
            {
                return central is null
                    ? Failed(job, "kind_not_allowed")
                    : await new BackfillRunner(central).RunAsync(job, client, cancellationToken);
            }

            var probe = await client.ProbeAsync(cancellationToken);
            var report = new ProbeReport(
                probe.Reachability.ToString(),
                probe.Tls.ToString(),
                probe.Authentication.ToString(),
                probe.System?.Firmware,
                probe.System?.Serial,
                probe.System?.SourceDeviceId,
                probe.ClockSkewSeconds,
                probe.System?.SslEnabled,
                probe.Failure?.ToString(),
                probe.CheckedAt);

            return new EdgeJobResult(job.FencingToken, EdgeJobOutcomes.Succeeded, null, report);
        }
        finally
        {
            await client.LogoutAsync(CancellationToken.None);
        }
    }

    private DeviceEndpoint? BuildEndpoint(EdgeDeviceTarget device, IPAddress address)
    {
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

        switch (device.TlsMode)
        {
            case DeviceTlsModes.PlainHttp:
                return options.AllowPlainHttp ? DeviceEndpoint.PlainHttp(new Uri($"http://{host}:{device.Port}/")) : null;
            case DeviceTlsModes.Pinned when device.TlsPin is { Length: > 0 } pin:
                return DeviceEndpoint.Https(new Uri($"https://{host}:{device.Port}/"), DeviceTlsPolicy.PinnedCertificate(pin));
            case DeviceTlsModes.System:
                return DeviceEndpoint.Https(new Uri($"https://{host}:{device.Port}/"), DeviceTlsPolicy.SystemTrust());
            case DeviceTlsModes.PrivateCa when privateCa?.Invoke() is { } policy:
                return DeviceEndpoint.Https(new Uri($"https://{host}:{device.Port}/"), policy);
            default:
                return null;
        }
    }

    private static EdgeJobResult Failed(EdgeJob job, string code) =>
        new(job.FencingToken, EdgeJobOutcomes.Failed, code, null);
}
