using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Api;
using AiControlCenter.Identity.Infrastructure;
using AiControlCenter.Identity.Infrastructure.Persistence;
using AiControlCenter.Observability;
using AiControlCenter.Security;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddExceptionHandler<IdentityExceptionHandler>();
builder.Services.AddApiFoundation();
//Цей extension method реєструє Application-компоненти в DI-контейнері. Валідатори
builder.Services.AddIdentityApplication();
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
//Метод реєструє повну JWT authentication для поточного сервісу.
builder.Services.AddPlatformAuthentication(builder.Configuration);
//перевіряє користувача
builder.Services.AddPlatformAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = ".AiControlCenter.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.HeaderName = "X-XSRF-TOKEN";
});
builder.Services.AddRateLimiter(options => options.AddPolicy("identity-auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        })));
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

var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"]
    ?? throw new InvalidOperationException("DataProtection:KeysPath is required.");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("AiControlCenter.Identity");
builder.Services.AddHostedService<BootstrapAdminHostedService>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IdentityDbContext>("identity-database", tags: ["ready"]);
builder.Services.AddValidatorsFromAssemblyContaining<IdentityApplicationMarker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
{
    Type = SecuritySchemeType.Http,
    Scheme = "bearer",
    BearerFormat = "JWT",
}));

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
    return;
}

app.UseForwardedHeaders();
app.UseApiFoundation();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

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
})).WithName("GetIdentityServiceInfo");
if (app.Environment.IsDevelopment())
{
    serviceInfo.AllowAnonymous();
}
else
{
    serviceInfo.RequireAuthorization(SecurityPolicyNames.AnyPlatformUser);
}

//Цей файл описує HTTP API Identity.
app.MapIdentityEndpoints(app.Environment);

app.Run();

public partial class Program;
