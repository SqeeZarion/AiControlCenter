using System.Net;
using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using AiControlCenter.Gateway.Messaging;
using AiControlCenter.Gateway.Operations;
using AiControlCenter.Observability;
using AiControlCenter.Security;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddPlatformAuthentication(builder.Configuration);
//перевіряє користувача
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
builder.Services.AddGatewayMessaging(builder.Configuration);
builder.Services.AddOptions<OperationsCenterOptions>()
    .Bind(builder.Configuration.GetSection(OperationsCenterOptions.SectionName))
    .Validate(options => options.RequestTimeoutSeconds is >= 1 and <= 10,
        "OperationsCenter:RequestTimeoutSeconds must be between 1 and 10.")
    .Validate(options => options.FreshnessSeconds is >= 10 and <= 300,
        "OperationsCenter:FreshnessSeconds must be between 10 and 300.")
    .Validate(options => options.Services.Count > 0
        && options.Services.All(item => !string.IsNullOrWhiteSpace(item.Value.Label)
            && Uri.TryCreate(item.Value.Address, UriKind.Absolute, out _)),
        "OperationsCenter:Services must contain labelled absolute service addresses.")
    .Validate(options => Uri.TryCreate(
            options.RabbitMqManagementAddress,
            UriKind.Absolute,
            out var address)
        && address.Scheme is "http" or "https",
        "OperationsCenter:RabbitMqManagementAddress must be an absolute HTTP(S) address.")
    .Validate(options => options.Queues is { Length: > 0 }
        && options.Queues.All(queue => !string.IsNullOrWhiteSpace(queue)),
        "OperationsCenter:Queues must contain non-empty queue names.")
    .ValidateOnStart();
builder.Services.AddHttpClient(OperationsCenterService.HttpClientName, client =>
    client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IOperationsCenterService, OperationsCenterService>();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHealthChecks()
    .AddCheck<YarpConfigurationHealthCheck>("yarp-configuration", tags: ["ready"]);
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
app.MapOperationsCenterEndpoints();
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
