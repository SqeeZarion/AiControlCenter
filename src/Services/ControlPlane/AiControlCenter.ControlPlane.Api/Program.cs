using AiControlCenter.ControlPlane.Api;
using AiControlCenter.ControlPlane.Api.Grpc;
using AiControlCenter.ControlPlane.Infrastructure;
using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using AiControlCenter.Observability;
using AiControlCenter.Security;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
//Метод реєструє повну JWT authentication для поточного сервісу.
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformAuthorization();
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
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapGrpcReflectionService();
}

app.MapDefaultHealthEndpoints();
app.MapGrpcService<ServiceInfoGrpcService>()
    .RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);
app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}))
.WithName("GetControlPlaneServiceInfo")
.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

app.Run();

public partial class Program;
