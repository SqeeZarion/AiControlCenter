using System.Text.Json;
using AiControlCenter.ControlPlane.Api;
using AiControlCenter.Identity.Api;
using AiControlCenter.Integrations.Api;
using AiControlCenter.Orchestrator.Api;
using AiControlCenter.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class ServiceHostHealthTests
{
    [Theory]
    [MemberData(nameof(PrivatePublicKeyPaths))]
    public async Task PrivatePemInPublicKeyPathFailsControlPlaneStartup(string publicKeyPath)
    {
        await using var factory = CreateControlPlaneFactory(publicKeyPath);

        await AssertStartupFailsAsync(factory, "Authentication:PublicKeyPath contains private key material");
    }

    [Fact]
    public async Task MalformedPublicKeyFailsControlPlaneStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aicontrolcenter-controlplane-malformed-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, "not-an-rsa-key");
        try
        {
            await using var factory = CreateControlPlaneFactory(path);
            await AssertStartupFailsAsync(factory, "valid RSA public PEM key");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string> PrivatePublicKeyPaths => new()
    {
        TestJwtTokenFactory.PrivatePkcs1KeyPath,
        TestJwtTokenFactory.PrivateKeyPath,
    };

    [Fact]
    public Task IdentityLivenessIsHealthy() => AssertLivenessAsync<IdentityApiMarker>(
        "ConnectionStrings:IdentityDatabase");

    [Fact]
    public Task ControlPlaneLivenessIsHealthy() => AssertLivenessAsync<ControlPlaneApiMarker>(
        "ConnectionStrings:ControlPlaneDatabase");

    [Fact]
    public Task IntegrationsLivenessIsHealthy() => AssertLivenessAsync<IntegrationsApiMarker>(
        "ConnectionStrings:IntegrationsDatabase");

    [Fact]
    public async Task IdentityReadinessReportsIdentityDatabaseAsUnhealthyWhenPostgreSqlIsUnavailable()
    {
        await using var factory = new WebApplicationFactory<IdentityApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting(
                    "ConnectionStrings:IdentityDatabase",
                    "Host=127.0.0.1;Port=1;Database=identity;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("JwtSigning:PrivateKeyPath", TestJwtTokenFactory.PrivateKeyPath)
                .UseSetting("IdentityBootstrap:Disabled", "true")
                .UseSetting(
                    "DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), $"aicontrolcenter-unavailable-db-dp-{Guid.NewGuid():N}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        await AssertHealthCheckStatusAsync(
            response,
            "identity-database",
            expectedStatus: "Unhealthy");
    }

    [Fact]
    public async Task OrchestratorReadinessReportsMassTransitRegistrationWhenRabbitMqIsUnavailable()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
            .WithDatabase("orchestrator_health_tests")
            .WithUsername("postgres")
            .WithPassword("test-superuser-password")
            .Build();
        await postgres.StartAsync();
        await using var factory = new WebApplicationFactory<OrchestratorApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:OrchestratorDatabase", postgres.GetConnectionString())
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("RabbitMq:Host", "127.0.0.1")
                .UseSetting("RabbitMq:Port", "1")
                .UseSetting("RabbitMq:Username", "unavailable")
                .UseSetting("RabbitMq:Password", "unavailable")
                .UseSetting("WorkerGrpc:ApiKey", "health-test-worker-key-at-least-32-characters"));
        using var client = factory.CreateClient();
        var registrations = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;
        var rabbitRegistration = Assert.Single(registrations, registration =>
            registration.Name.Contains("mass", StringComparison.OrdinalIgnoreCase)
            || registration.Name.Contains("bus", StringComparison.OrdinalIgnoreCase));

        using var response = await client.GetAsync("/health/ready");

        await AssertHealthCheckStatusAsync(response, "orchestrator-database", expectedStatus: "Healthy");
        await AssertHealthCheckStatusAsync(response, rabbitRegistration.Name, expectedStatus: "Unhealthy");
    }

    private static async Task AssertLivenessAsync<TEntryPoint>(string connectionStringKey)
        where TEntryPoint : class
    {
        await using var factory = new WebApplicationFactory<TEntryPoint>()
            .WithWebHostBuilder(builder => builder
                .UseSetting(connectionStringKey,
                    "Host=127.0.0.1;Port=1;Database=foundation;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("JwtSigning:PrivateKeyPath", TestJwtTokenFactory.PrivateKeyPath)
                .UseSetting("IdentityBootstrap:Disabled", "true")
                .UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), "aicontrolcenter-dp-tests")));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task IntegrationsDirectServiceInfoRequiresAuthentication()
    {
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase", "Host=127.0.0.1;Port=1;Database=foundation;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/service-info");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static WebApplicationFactory<ControlPlaneApiMarker> CreateControlPlaneFactory(string publicKeyPath) =>
        new WebApplicationFactory<ControlPlaneApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting(
                    "ConnectionStrings:ControlPlaneDatabase",
                    "Host=127.0.0.1;Port=1;Database=foundation;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", publicKeyPath));

    private static async Task AssertStartupFailsAsync(
        WebApplicationFactory<ControlPlaneApiMarker> factory,
        string expectedReason)
    {
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health/live");
        });
        var validation = FindOptionsValidationException(exception);

        Assert.True(validation is not null, exception?.ToString());
        Assert.Equal(typeof(PlatformAuthenticationOptions), validation.OptionsType);
        Assert.Contains(validation.Failures, failure =>
            failure.Contains(expectedReason, StringComparison.OrdinalIgnoreCase));
    }

    private static OptionsValidationException? FindOptionsValidationException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is OptionsValidationException validation)
        {
            return validation;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(FindOptionsValidationException)
                .FirstOrDefault(candidate => candidate is not null);
        }

        return FindOptionsValidationException(exception.InnerException);
    }

    private static async Task AssertHealthCheckStatusAsync(
        HttpResponseMessage response,
        string registrationName,
        string expectedStatus)
    {
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Contains(checks, check =>
            check.GetProperty("name").GetString() == registrationName
            && check.GetProperty("status").GetString() == expectedStatus);
    }
}
