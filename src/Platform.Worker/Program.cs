using Npgsql;
using Platform.Modules.Importation;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Storage;
using Platform.Worker;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Hub")
    ?? throw new InvalidOperationException("ConnectionStrings:Hub nao configurada.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ITenantDatabase, TenantDatabase>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Inspection").Get<InspectionLimits>() ?? new InspectionLimits());
builder.Services.AddHubStorage(builder.Configuration, builder.Environment);

// A previa nao depende do antivirus: so processa arquivos que ja passaram pela inspecao.
builder.Services.AddSingleton(builder.Configuration.GetSection("Preview").Get<PreviewLimits>() ?? new PreviewLimits());
if (!string.IsNullOrWhiteSpace(builder.Configuration["Kms:Address"]))
{
    builder.Services.AddSingleton<ImportPreviewService>();
    builder.Services.AddHostedService<ImportPreviewWorker>();
}

// Sem scanner nao ha inspecao: os arquivos esperam na quarentena, em vez de passar sem antivirus.
var scannerHost = builder.Configuration["Scanner:Host"];
if (!string.IsNullOrWhiteSpace(scannerHost))
{
    var scannerPort = builder.Configuration.GetValue("Scanner:Port", 3310);
    builder.Services.AddSingleton<IMalwareScanner>(new ClamAvScanner(scannerHost, scannerPort));
    builder.Services.AddSingleton<ImportInspectionService>();
    builder.Services.AddHostedService<ServiceHeartbeat>();
}
else
{
    builder.Services.AddHostedService<IdleWithoutScanner>();
}

await builder.Build().RunAsync();
