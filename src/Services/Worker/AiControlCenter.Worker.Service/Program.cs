using AiControlCenter.Observability;
using AiControlCenter.Worker.Service;
using AiControlCenter.Worker.Service.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddRabbitMqTransport(builder.Configuration);
builder.Services.AddHostedService<Worker>();

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
