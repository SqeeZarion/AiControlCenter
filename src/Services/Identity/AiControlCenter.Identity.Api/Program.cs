using AiControlCenter.Identity.Api;
using AiControlCenter.Identity.Infrastructure;
using AiControlCenter.Identity.Infrastructure.Persistence;
using AiControlCenter.Observability;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IdentityDbContext>("identity-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<IdentityApiMarker>();
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
.WithName("GetIdentityServiceInfo");

app.Run();

public partial class Program;
