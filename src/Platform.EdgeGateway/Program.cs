using System.Security.Cryptography.X509Certificates;
using Platform.EdgeGateway;
using Platform.Modules.DeviceIntegration.ControlId;

var builder = Host.CreateApplicationBuilder(args);

var options = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
if (options.CentralUrl is not { Scheme: "https" })
{
    throw new InvalidOperationException("Gateway:CentralUrl precisa ser um endereco HTTPS.");
}

var identity = GatewayIdentity.LoadOrCreate(options.IdentityPath);
var centralRoot = options.CentralCaPath is { Length: > 0 } caPath ? X509CertificateLoader.LoadCertificateFromFile(caPath) : null;
var deviceRoot = options.PrivateCaPath is { Length: > 0 } devicePath ? X509CertificateLoader.LoadCertificateFromFile(devicePath) : null;

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ICentralClient>(_ => new CentralClient(options.CentralUrl, identity, centralRoot));
builder.Services.AddSingleton(provider => new JobExecutor(
    options,
    provider.GetRequiredService<ILoggerFactory>(),
    deviceRoot is null ? null : () => DeviceTlsPolicy.PrivateCa(deviceRoot),
    provider.GetRequiredService<ICentralClient>()));

var spool = new EventSpool(options.SpoolPath, options.SpoolMaxEvents);
var directory = new DeviceDirectory();
builder.Services.AddSingleton(spool);
builder.Services.AddSingleton(directory);
builder.Services.AddSingleton(new MonitorCounters());
builder.Services.AddSingleton(provider => new GatewayAgent(
    provider.GetRequiredService<ICentralClient>(),
    provider.GetRequiredService<JobExecutor>(),
    options,
    provider.GetRequiredService<ILogger<GatewayAgent>>(),
    null,
    spool,
    directory));
builder.Services.AddHostedService<ServiceHeartbeat>();
if (options.MonitorEnabled)
{
    builder.Services.AddHostedService<MonitorListenerService>();
}

await builder.Build().RunAsync();
