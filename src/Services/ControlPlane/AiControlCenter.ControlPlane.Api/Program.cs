using AiControlCenter.ControlPlane.Api;
using AiControlCenter.ControlPlane.Api.Grpc;
using AiControlCenter.ControlPlane.Infrastructure;
using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using AiControlCenter.Observability;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddControlPlaneInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ControlPlaneDbContext>("control-plane-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<ControlPlaneApiMarker>();
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseApiFoundation();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapGrpcReflectionService();
}

app.MapDefaultHealthEndpoints();
app.MapGrpcService<ServiceInfoGrpcService>();
app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}))
.WithName("GetControlPlaneServiceInfo");

app.Run();

public partial class Program;
