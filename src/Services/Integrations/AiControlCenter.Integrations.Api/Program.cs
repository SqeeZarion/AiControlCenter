using AiControlCenter.Integrations.Api;
using AiControlCenter.Integrations.Infrastructure;
using AiControlCenter.Integrations.Infrastructure.Persistence;
using AiControlCenter.Observability;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddIntegrationsInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IntegrationsDbContext>("integrations-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<IntegrationsApiMarker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseApiFoundation();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultHealthEndpoints();
app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}))
.WithName("GetIntegrationsServiceInfo");

app.Run();

public partial class Program;
