using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Api;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Infrastructure;
using AiControlCenter.Integrations.Infrastructure.Persistence;
using AiControlCenter.Observability;
using AiControlCenter.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddExceptionHandler<IntegrationsExceptionHandler>();
builder.Services.AddApiFoundation();
builder.Services.AddPlatformAuthentication(builder.Configuration);
//перевіряє користувача
builder.Services.AddPlatformAuthorization();
builder.Services.AddIntegrationsInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IProviderMetadataCanonicalizer>(new ProviderMetadataCanonicalizer(
    ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
    ProviderCheckpointMetadataContract.CanonicalizeActualModel,
    CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes));
builder.Services.AddSingleton<LocalCopilotReconnectCoordinator>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IntegrationsDbContext>("integrations-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<IntegrationsApiMarker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("provider-credentials", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
builder.Services.AddGrpc();
builder.Services.AddSingleton<IValidateOptions<InternalGrpcOptions>, InternalGrpcOptionsValidator>();
builder.Services.AddOptions<InternalGrpcOptions>()
    .Bind(builder.Configuration.GetSection(InternalGrpcOptions.SectionName)).ValidateOnStart();

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>().Database.MigrateAsync();
    return;
}

app.UseApiFoundation();
app.UseMiddleware<SafeRequestLoggingMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultHealthEndpoints();
app.MapProviderConnectionEndpoints();
app.MapGrpcService<AiProviderExecutionGrpcService>()
    .AllowAnonymous();
app.MapGet("/service-info", (IHostEnvironment environment) => Results.Ok(new
{
    service = environment.ApplicationName,
    version = "v1",
    environment = environment.EnvironmentName,
}))
.WithName("GetIntegrationsServiceInfo")
.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

app.Run();

public partial class Program;
