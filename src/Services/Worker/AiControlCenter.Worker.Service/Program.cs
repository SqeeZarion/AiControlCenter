using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Observability;
using AiControlCenter.Worker.Service;
using AiControlCenter.Worker.Service.Messaging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddRabbitMqTransport(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IValidateOptions<WorkerExecutionOptions>, WorkerExecutionOptionsValidator>();
builder.Services.AddOptions<WorkerExecutionOptions>()
    .Bind(builder.Configuration.GetSection(WorkerExecutionOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddGrpcClient<RunProgress.RunProgressClient>(options =>
{
    options.Address = new Uri(builder.Configuration["OrchestratorGrpc:Address"]
        ?? "http://orchestrator-api:8081");
});

var app = builder.Build();

app.UseApiFoundation();
app.MapDefaultHealthEndpoints();
app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}));

app.Run();

public partial class Program;
