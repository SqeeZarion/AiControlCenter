using System.Text.Json.Serialization;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Observability;
using AiControlCenter.Orchestrator.Api;
using AiControlCenter.Orchestrator.Api.Grpc;
using AiControlCenter.Orchestrator.Api.Messaging;
using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Infrastructure;
using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using AiControlCenter.Security;
using FluentValidation;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

//підключає спільні можливості сервісів
builder.AddServiceDefaults();
builder.Services.AddExceptionHandler<OrchestratorExceptionHandler>();
builder.Services.AddApiFoundation();
//Метод реєструє повну JWT authentication для поточного сервісу.
builder.Services.AddPlatformAuthentication(builder.Configuration);
//перевіряє користувача
builder.Services.AddPlatformAuthorization();
builder.Services.AddOrchestratorApplication();
builder.Services.AddOrchestratorInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrchestratorDbContext>("orchestrator-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<OrchestratorApiMarker>();
builder.Services.AddRabbitMqTransport(builder.Configuration);
builder.Services.AddScoped<IRunTransport, AgentRunTransport>();
builder.Services.AddScoped<IAgentCatalogClient, AgentCatalogClient>();
builder.Services.AddSingleton<IValidateOptions<WorkerGrpcOptions>, WorkerGrpcOptionsValidator>();
builder.Services.AddOptions<WorkerGrpcOptions>()
    .Bind(builder.Configuration.GetSection(WorkerGrpcOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<AgentCatalog.AgentCatalogClient>(options =>
{
    var address = builder.Configuration["ControlPlane:GrpcAddress"]
        ?? "http://controlplane-api:8081";
    options.Address = new Uri(address);
});
builder.Services.AddGrpcClient<ServiceInfo.ServiceInfoClient>(options =>
{
    var address = builder.Configuration["ControlPlane:GrpcAddress"]
        ?? "http://controlplane-api:8081";
    options.Address = new Uri(address);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
    return;
}

app.UseApiFoundation();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultHealthEndpoints();
app.MapAgentRunEndpoints();
app.MapGrpcService<RunProgressGrpcService>().AllowAnonymous();
app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}))
.WithName("GetOrchestratorServiceInfo")
.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

app.MapGet(
    "/service-info/control-plane",
    async (HttpContext context, ServiceInfo.ServiceInfoClient client, CancellationToken cancellationToken) =>
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }

        var metadata = new Metadata { { "Authorization", authorization } };
        var reply = await client.GetServiceInfoAsync(
            new ServiceInfoRequest { Caller = "orchestrator" },
            headers: metadata,
            cancellationToken: cancellationToken);

        return Results.Ok(new
        {
            service = reply.ServiceName,
            version = reply.Version,
            environment = reply.Environment,
        });
    })
.WithName("GetControlPlaneServiceInfoViaGrpc")
.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

app.Run();

public partial class Program;
