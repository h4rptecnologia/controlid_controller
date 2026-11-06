using Microsoft.AspNetCore.Authorization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using Platform.Api.Hosting;
using Platform.Modules.Fleet;
using Platform.Modules.Governance;
using Platform.Modules.IdentityTenancy;
using Platform.Modules.Importation;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Kernel.Data;
using Platform.Shared.Kernel.Http;
using Platform.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var problem = context.ProblemDetails;
    problem.Extensions.TryAdd("code", Problems.CodeForStatus(problem.Status ?? context.HttpContext.Response.StatusCode));
    problem.Extensions.TryAdd("correlationId", context.HttpContext.GetCorrelationId());
});
builder.Services.AddOpenApi();

// Corpo de escrita pequeno por padrao; upload de arquivo tera rota propria com limite proprio (E03.1).
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);

// Validacao estrita: campo desconhecido e erro, e nao fica silenciosamente ignorado.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddHttpContextAccessor();

builder.ConfigureEdgeListener();
builder.Services.AddHubAuthentication(builder.Configuration);
builder.Services.AddHubRateLimiting(builder.Configuration);

// Fail-closed: tudo exige autenticacao salvo opt-out explicito.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddSingleton(provider =>
    NpgsqlDataSource.Create(provider.GetRequiredService<IConfiguration>().GetConnectionString("Hub")
        ?? throw new InvalidOperationException("ConnectionStrings:Hub nao configurada.")));
builder.Services.AddSingleton<ITenantDatabase, TenantDatabase>();
builder.Services.AddScoped<RequestAccess>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Inspection").Get<InspectionLimits>() ?? new InspectionLimits());

builder.Services.AddSingleton(builder.Configuration.GetSection("ImportApproval").Get<ImportApprovalOptions>() ?? new ImportApprovalOptions());

builder.Services.AddHealthChecks()
    .AddCheck<PostgresReadinessCheck>("postgres", tags: ["ready"]);
builder.Services.AddHubStorage(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseHubSecurityHeaders();
app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseCsrfProtection();
app.UseAuthorization();
app.UseIdempotencyBuffering();

// Health responde apenas o status agregado: nenhum detalhe de infraestrutura e exposto.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSiteEndpoints();
app.MapActorEndpoints();
app.MapKeyEndpoints();
app.MapAuditEndpoints();
app.MapFleetEndpoints();
app.MapEventQueryEndpoints();
app.MapBackfillEndpoints();
app.MapImportEndpoints();
app.MapImportRunEndpoints();
app.MapEdgeEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

await app.RunAsync();
