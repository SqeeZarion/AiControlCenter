using System.Net;
using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using AiControlCenter.Observability;
using AiControlCenter.Security;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformAuthorization();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(IPAddress.Parse(value));
    }
});
builder.Services.AddValidatorsFromAssemblyContaining<GatewayMarker>();
builder.Services.AddSignalR();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseApiFoundation();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultHealthEndpoints();
var serviceInfo = app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
})).WithName("GetGatewayServiceInfo");
var publicServiceInfo = app.MapGet("/api/gateway/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
})).WithName("GetPublicGatewayServiceInfo");
if (app.Environment.IsDevelopment())
{
    serviceInfo.AllowAnonymous();
    publicServiceInfo.AllowAnonymous();
}
else
{
    serviceInfo.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser);
    publicServiceInfo.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser);
}

app.MapHub<SystemHub>("/hubs/system", options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization(SecurityPolicyNames.PasswordChanged);
app.MapReverseProxy();

app.Run();

public partial class Program;
