using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiControlCenter.Integrations.Api;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Persistence;
using AiControlCenter.Integrations.Infrastructure.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class GitHubCopilotValidationCompositionTests : IAsyncLifetime
{
    private const string WorkerKey = "integration-test-internal-grpc-key-32-chars";
    private const string TestCredential = "github_pat_test-validation-token-never-leaves-test-process";
    private const string FineGrainedPat = "github_pat_test-user-token-never-leaves-test-process";
    private static readonly JsonSerializerOptions ResponseJsonOptions = CreateResponseJsonOptions();
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("github_validation_composition")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();
    private readonly DirectoryInfo dataProtectionDirectory =
        Directory.CreateTempSubdirectory("aicontrolcenter-github-validation-dp-");

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var database = CreateDbContext();
        await database.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync();
        dataProtectionDirectory.Delete(recursive: true);
    }

    [Fact]
    public async Task RestValidateDecryptsLatestStoredCredentialAndPersistsIdentityCatalogAndActiveStatus()
    {
        var runtime = new CompositionCopilotRuntime();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:IntegrationsDatabase", postgres.GetConnectionString())
                    .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                    .UseSetting("DataProtection:KeysPath", dataProtectionDirectory.FullName)
                    .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                    .UseSetting("Providers:GitHub:Enabled", "false")
                    .UseSetting("ProviderConnectionFeatures:GitHubCopilotPatEnabled", "true");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ICopilotRuntime>();
                    services.AddSingleton<ICopilotRuntime>(runtime);
                    services.AddHttpClient<GitHubCopilotProvider>()
                        .ConfigurePrimaryHttpMessageHandler(() => new GitHubIdentityHandler(TestCredential));
                });
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(userId: Guid.NewGuid(), role: "Developer"));

        using var createResponse = await client.PutAsJsonAsync(
            "/v1/provider-connections/github/pat",
            new { personalAccessToken = TestCredential, expectedGitHubLogin = "student-user" });
        var created = await createResponse.Content.ReadFromJsonAsync<ProviderConnectionDto>(ResponseJsonOptions);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.NotNull(created);

        using var validateResponse = await client.PostAsync(
            $"/v1/provider-connections/{created.Id}/validate", null);
        var validated = await validateResponse.Content.ReadFromJsonAsync<ProviderConnectionDto>(ResponseJsonOptions);

        Assert.Equal(HttpStatusCode.OK, validateResponse.StatusCode);
        Assert.NotNull(validated);
        Assert.Equal(ProviderConnectionStatus.Active, validated.Status);
        Assert.Equal("24680", validated.ExternalAccountId);
        Assert.Equal("student-user", validated.ExternalAccountLogin);
        Assert.NotNull(validated.LastValidatedAt);
        Assert.NotNull(validated.LastSyncedAt);
        Assert.Equal(TestCredential, runtime.AccessToken);
        Assert.DoesNotContain(TestCredential, await validateResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using var verification = CreateDbContext();
        var persisted = await verification.ProviderConnections.AsNoTracking()
            .SingleAsync(item => item.Id == created.Id);
        Assert.Equal(ProviderConnectionStatus.Active, persisted.Status);
        Assert.Equal("student-user", persisted.ExternalAccountLogin);
        Assert.Contains("copilot-student-model", persisted.AvailableModelsJson, StringComparison.Ordinal);
        Assert.NotEqual(TestCredential, persisted.ProtectedCredential);
    }

    [Fact]
    public async Task RestConnectGitHubPatValidatesBeforePersistingAndStoresOnlyProtectedCredential()
    {
        var runtime = new CompositionCopilotRuntime();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:IntegrationsDatabase", postgres.GetConnectionString())
                    .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                    .UseSetting("DataProtection:KeysPath", dataProtectionDirectory.FullName)
                    .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                    .UseSetting("Providers:GitHub:Enabled", "false")
                    .UseSetting("ProviderConnectionFeatures:GitHubCopilotPatEnabled", "true");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ICopilotRuntime>();
                    services.AddSingleton<ICopilotRuntime>(runtime);
                    services.AddHttpClient<GitHubCopilotProvider>()
                        .ConfigurePrimaryHttpMessageHandler(() => new GitHubIdentityHandler(FineGrainedPat));
                });
            });
        var owner = Guid.NewGuid();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(userId: owner, role: "Developer"));

        using var connectResponse = await client.PutAsJsonAsync(
            "/v1/provider-connections/github/pat",
            new
            {
                personalAccessToken = $" \r\n{FineGrainedPat}\r\n ",
                expectedGitHubLogin = "  @student-user  ",
            });
        var responseBody = await connectResponse.Content.ReadAsStringAsync();
        var connected = JsonSerializer.Deserialize<ProviderConnectionDto>(responseBody, ResponseJsonOptions);

        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        Assert.NotNull(connected);
        Assert.Equal(ProviderConnectionStatus.Active, connected.Status);
        Assert.Equal("24680", connected.ExternalAccountId);
        Assert.Equal("student-user", connected.ExternalAccountLogin);
        Assert.NotNull(connected.LastValidatedAt);
        Assert.NotNull(connected.LastSyncedAt);
        Assert.Equal(FineGrainedPat, runtime.AccessToken);
        Assert.DoesNotContain(FineGrainedPat, responseBody, StringComparison.Ordinal);

        await using var verification = CreateDbContext();
        var persisted = await verification.ProviderConnections.AsNoTracking()
            .SingleAsync(item => item.OwnerUserId == owner);
        Assert.NotEqual(FineGrainedPat, persisted.ProtectedCredential);
        Assert.DoesNotContain(FineGrainedPat, persisted.ProtectedCredential, StringComparison.Ordinal);
        Assert.Equal(ProviderConnectionStatus.Active, persisted.Status);
        Assert.Contains("copilot-student-model", persisted.AvailableModelsJson, StringComparison.Ordinal);
    }

    private IntegrationsDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "integrations"))
            .Options);

    private static JsonSerializerOptions CreateResponseJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class GitHubIdentityHandler(string expectedCredential) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/user", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedCredential, request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = 24680, login = "student-user" }),
            });
        }
    }

    private sealed class CompositionCopilotRuntime : ICopilotRuntime
    {
        public string? AccessToken { get; private set; }

        public Task<CopilotRuntimeInspection> InspectAsync(string accessToken, CancellationToken token)
        {
            AccessToken = accessToken;
            return Task.FromResult(new CopilotRuntimeInspection(
                [new("copilot-student-model", "Copilot Student", null, true, true, null, null)],
                ProviderMetricSource.Unavailable, "Unavailable"));
        }

        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(
            string accessToken,
            CancellationToken token) => Task.FromResult<IReadOnlyCollection<ProviderModelDto>>(
                [new("copilot-student-model", "Copilot Student", null, true, true, null, null)]);

        public Task<CopilotRuntimeResult> ExecuteAsync(string accessToken, string model,
            string systemPrompt, string input, IReadOnlyCollection<ToolDefinition> tools,
            IReadOnlyCollection<ToolInvocationResult> priorResults, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
