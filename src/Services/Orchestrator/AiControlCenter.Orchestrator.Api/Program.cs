using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Observability;
using AiControlCenter.Orchestrator.Api;
using AiControlCenter.Orchestrator.Api.Messaging;
using AiControlCenter.Orchestrator.Infrastructure;
using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddOrchestratorInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrchestratorDbContext>("orchestrator-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<OrchestratorApiMarker>();
builder.Services.AddRabbitMqTransport(builder.Configuration);
builder.Services.AddGrpcClient<ServiceInfo.ServiceInfoClient>(options =>
{
    var address = builder.Configuration["ControlPlane:GrpcAddress"]
        ?? "http://controlplane-api:8081";
    options.Address = new Uri(address);
});
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
.WithName("GetOrchestratorServiceInfo");

app.MapGet(
    "/service-info/control-plane",
    async (ServiceInfo.ServiceInfoClient client, CancellationToken cancellationToken) =>
    {
        var reply = await client.GetServiceInfoAsync(
            new ServiceInfoRequest { Caller = "orchestrator" },
            cancellationToken: cancellationToken);

        return Results.Ok(new
        {
            service = reply.ServiceName,
            version = reply.Version,
            environment = reply.Environment,
        });
    })
.WithName("GetControlPlaneServiceInfoViaGrpc");

app.Run();

public partial class Program;
