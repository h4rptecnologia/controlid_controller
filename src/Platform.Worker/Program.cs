using Platform.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ServiceHeartbeat>();

await builder.Build().RunAsync();
