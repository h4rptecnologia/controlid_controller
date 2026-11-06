namespace Platform.Modules.DeviceIntegration.ControlId;

public enum DeviceFailure
{
    /// <summary>Nao foi possivel abrir conexao.</summary>
    Unreachable,

    /// <summary>Certificado recusado. Nenhuma credencial foi enviada.</summary>
    TlsRejected,

    /// <summary>Login recusado pelo equipamento.</summary>
    AuthenticationFailed,

    /// <summary>Sessao recusada em uma mutacao, que nunca e repetida automaticamente.</summary>
    SessionExpired,

    /// <summary>Sem resposta no prazo. Em mutacao, nao se sabe se o efeito ocorreu.</summary>
    Timeout,

    /// <summary>O equipamento respondeu com erro.</summary>
    DeviceError,

    /// <summary>Resposta fora do contrato ou grande demais.</summary>
    InvalidResponse,

    /// <summary>
    /// A resposta e valida, mas o equipamento ignorou filtro, ordem ou limite. A leitura para em vez de
    /// repetir dados: indica firmware com contrato diferente do documentado.
    /// </summary>
    UnexpectedBehavior,
}

/// <summary>A mensagem e fixa de proposito: nunca carrega URL, sessao ou corpo da resposta.</summary>
public sealed class DeviceCallException : Exception
{
    public DeviceCallException()
        : base("Falha na comunicacao com o equipamento.")
    {
    }

    public DeviceCallException(string message)
        : base(message)
    {
    }

    public DeviceCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DeviceCallException(DeviceFailure failure, int? httpStatus = null)
        : base($"Falha na comunicacao com o equipamento: {failure}.")
    {
        Failure = failure;
        HttpStatus = httpStatus;
    }

    public DeviceFailure Failure { get; }

    public int? HttpStatus { get; }
}

/// <summary>Credencial do equipamento. ToString nunca revela a senha, nem por acidente em log.</summary>
public sealed class DeviceCredentials(string login, string password)
{
    internal string Login { get; } = login;

    internal string Password { get; } = password;

    public override string ToString() => "DeviceCredentials(***)";
}

/// <summary>
/// Campos de POST /system_information.fcgi conforme a documentacao oficial. O iDCloud_code fica de fora de
/// proposito: e um codigo de autenticacao e nao deve circular nem ser persistido.
/// O modelo do equipamento nao aparece nesta resposta e nao e inferido.
/// </summary>
public sealed record DeviceSystemInfo(
    string? Firmware,
    string? Serial,
    string? SourceDeviceId,
    long? DeviceTimeUnix,
    bool? SslEnabled,
    int? WebServerPort,
    bool? Online,
    bool? OnlineAvailable);

public enum ProbeReachability
{
    Unknown,
    Reachable,
    Unreachable,
}

public enum ProbeTls
{
    Unknown,
    Verified,
    Rejected,

    /// <summary>HTTP puro, permitido por configuracao explicita. Risco aberto (docs/06-seguranca.md).</summary>
    NotUsed,
}

public enum ProbeAuthentication
{
    NotTested,
    Valid,
    Rejected,
}

/// <summary>
/// Diagnostico com cada etapa separada (RF-004). Nao declara capacidades: uma resposta aqui nao homologa
/// modelo nem firmware, e a matriz de capacidades so aceita evidencia em hardware.
/// </summary>
public sealed record DeviceProbeResult(
    ProbeReachability Reachability,
    ProbeTls Tls,
    ProbeAuthentication Authentication,
    DeviceSystemInfo? System,
    long? ClockSkewSeconds,
    DeviceFailure? Failure,
    DateTimeOffset CheckedAt);

/// <summary>Toda a aplicacao fala com o equipamento por aqui; rotas do fabricante nao saem deste modulo.</summary>
public interface IDeviceConnector
{
    Task<DeviceProbeResult> ProbeAsync(CancellationToken cancellationToken);

    Task<DeviceSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken);

    Task<bool> IsSessionValidAsync(CancellationToken cancellationToken);

    Task LogoutAsync(CancellationToken cancellationToken);

    /// <summary>Uma pagina, com novas tentativas so para falhas transitorias.</summary>
    Task<DeviceObjectPage> LoadObjectsPageAsync(LoadObjectsRequest request, CancellationToken cancellationToken);

    /// <summary>Todas as paginas a partir do cursor. Persistir NextCursor de cada uma permite retomar.</summary>
    IAsyncEnumerable<DeviceObjectPage> ReadAllObjectsAsync(LoadObjectsRequest request, CancellationToken cancellationToken);
}
