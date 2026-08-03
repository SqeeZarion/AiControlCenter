using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using AiControlCenter.Observability;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddValidatorsFromAssemblyContaining<GatewayMarker>();
builder.Services.AddSignalR();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
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
.WithName("GetGatewayServiceInfo");
app.MapGet("/api/gateway/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}))
.WithName("GetPublicGatewayServiceInfo");
app.MapHub<SystemHub>("/hubs/system");
app.MapReverseProxy();

app.Run();

public partial class Program;
