namespace Platform.Shared.Contracts;

// API propria entre gateway e central (docs/12-contratos.md). Nao confundir com as rotas do fabricante.
public sealed record EnrollRequest(string Token, string? Version);

public sealed record EnrollResponse(Guid GatewayId, Guid SiteId);

public sealed record HeartbeatRequest(string? Version, int SpoolDepth, DateTimeOffset GatewayClockUtc);

public sealed record HeartbeatResponse(DateTimeOffset ServerTimeUtc);

/// <summary>Credencial entregue so na resposta do lease. Nunca e gravada na fila nem em log.</summary>
public sealed record EdgeDeviceLogin(string Login, string Password)
{
    public override string ToString() => "EdgeDeviceLogin(***)";
}

public sealed record EdgeDeviceTarget(Guid DeviceId, string Host, int Port, string TlsMode, string? TlsPin, EdgeDeviceLogin? Login);

public sealed record EdgeJob(Guid Id, string Kind, long FencingToken, DateTimeOffset LeaseUntil, DateTimeOffset? ExpiresAt, EdgeDeviceTarget? Device, EdgeBackfillPlan? Backfill = null);

public sealed record EdgeJobLease(EdgeJob? Job);

/// <summary>Campos validados do diagnostico. Sem espaco para payload livre do equipamento.</summary>
public sealed record ProbeReport(
    string Reachability,
    string Tls,
    string Authentication,
    string? Firmware,
    string? Serial,
    string? SourceDeviceId,
    long? ClockSkewSeconds,
    bool? SslEnabled,
    string? Failure,
    DateTimeOffset CheckedAt);

public sealed record EdgeJobResult(long FencingToken, string Outcome, string? FailureCode, ProbeReport? Probe, BackfillReport? Backfill = null);

public static class EdgeJobKinds
{
    public const string DeviceProbe = "device.probe";
    public const string DeviceBackfill = "device.backfill";
}

public static class EdgeJobOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class DeviceTlsModes
{
    public const string Pinned = "pinned";
    public const string System = "system";
    public const string PrivateCa = "private_ca";
    public const string PlainHttp = "plain_http";
}
