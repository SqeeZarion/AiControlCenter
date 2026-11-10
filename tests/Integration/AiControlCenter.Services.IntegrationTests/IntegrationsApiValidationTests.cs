using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Api;
using AiControlCenter.Integrations.Infrastructure.Providers;
using AiControlCenter.Orchestrator.Api;
using AiControlCenter.Orchestrator.Api.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class IntegrationsApiValidationTests
{
    private const string WorkerKey = "integration-test-internal-grpc-key-32-chars";

    [Fact]
    public async Task DisabledGitHubOAuthReturnsTypedNotConfiguredWithoutDatabaseAccess()
    {
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-oauth-disabled-dp"))
                .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                .UseSetting("Providers:GitHub:Enabled", "false")
                .UseSetting("ProviderConnectionFeatures:GitHubCopilotOAuthEnabled", "false"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(role: "Developer"));

        using var response = await client.PostAsJsonAsync(
            "/v1/provider-connections/github/oauth/start", new { });
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("GitHubCopilotOAuthDisabled", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("clientSecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapabilitiesAndDisabledCredentialEndpointsFailClosedWithoutDatabaseAccess()
    {
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-capabilities-dp"))
                .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                .UseSetting("Providers:GitHub:Enabled", "false")
                .UseSetting("ProviderConnectionFeatures:GitHubCopilotOAuthEnabled", "false")
                .UseSetting("ProviderConnectionFeatures:GitHubCopilotPatEnabled", "false")
                .UseSetting("ProviderConnectionFeatures:LocalCopilotCliEnabled", "true"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(role: "Developer"));

        using var capabilities = await client.GetAsync("/v1/provider-connections/capabilities");
        using var pat = await client.PutAsJsonAsync("/v1/provider-connections/github/pat", new
        {
            personalAccessToken = "github_pat_fictional_value_never_persisted",
            expectedGitHubLogin = "test-user",
        });
        using var patJson = JsonDocument.Parse(await pat.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        var capabilityBody = await capabilities.Content
            .ReadFromJsonAsync<ProviderConnectionCapabilitiesDto>();
        Assert.NotNull(capabilityBody);
        Assert.False(capabilityBody.GitHubCopilotOAuthEnabled);
        Assert.False(capabilityBody.GitHubCopilotPatEnabled);
        Assert.False(capabilityBody.LocalCopilotCliEnabled);
        Assert.False(capabilityBody.LocalCopilotCliReauthenticationEnabled);
        Assert.False(capabilityBody.LocalCopilotCliLogoutEnabled);
        Assert.Equal(HttpStatusCode.Conflict, pat.StatusCode);
        Assert.Equal("GitHubCopilotPatDisabled", patJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LocalCopilotReconnectIsHiddenFromEveryUserExceptConfiguredOwner()
    {
        var configuredOwner = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-local-owner-dp"))
                .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                .UseSetting("Providers:GitHub:Enabled", "false")
                .UseSetting("ProviderConnectionFeatures:LocalCopilotCliEnabled", "true")
                .UseSetting("Providers:LocalCopilotCli:Enabled", "true")
                .UseSetting("Providers:LocalCopilotCli:Endpoint", "local-copilot-cli:4321")
                .UseSetting("Providers:LocalCopilotCli:ManagementEndpoint", "http://local-copilot-cli:4322")
                .UseSetting("Providers:LocalCopilotCli:ConnectionToken",
                    "integration-test-local-copilot-token-32-characters")
                .UseSetting("Providers:LocalCopilotCli:OwnerUserId", configuredOwner.ToString()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(userId: Guid.NewGuid(), role: "Developer"));

        using var response = await client.PostAsync(
            "/v1/provider-connections/local-copilot/reconnect", null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var force = await client.PostAsJsonAsync(
            "/v1/provider-connections/local-copilot/reauthenticate", new { confirmed = true });
        using var forceJson = JsonDocument.Parse(await force.Content.ReadAsStringAsync());
        using var current = await client.GetAsync(
            "/v1/provider-connections/local-copilot/reconnect/current");
        using var logout = await client.PostAsJsonAsync(
            "/v1/provider-connections/local-copilot/logout", new { confirmed = true });
        using var capabilities = await client.GetAsync("/v1/provider-connections/capabilities");
        var capabilityBody = await capabilities.Content
            .ReadFromJsonAsync<ProviderConnectionCapabilitiesDto>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LocalCopilotCliNotAvailable", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, force.StatusCode);
        Assert.Equal("LocalCopilotCliNotAvailable",
            forceJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        Assert.NotNull(capabilityBody);
        Assert.False(capabilityBody.LocalCopilotCliEnabled);
    }

    [Fact]
    public async Task UnsupportedLocalCopilotReauthenticationAndLogoutFailClosed()
    {
        var configuredOwner = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-local-confirm-dp"))
                .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                .UseSetting("Providers:GitHub:Enabled", "false")
                .UseSetting("ProviderConnectionFeatures:LocalCopilotCliEnabled", "true")
                .UseSetting("Providers:LocalCopilotCli:Enabled", "true")
                .UseSetting("Providers:LocalCopilotCli:Endpoint", "local-copilot-cli:4321")
                .UseSetting("Providers:LocalCopilotCli:ManagementEndpoint", "http://local-copilot-cli:4322")
                .UseSetting("Providers:LocalCopilotCli:ConnectionToken",
                    "integration-test-local-copilot-token-32-characters")
                .UseSetting("Providers:LocalCopilotCli:OwnerUserId", configuredOwner.ToString()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(userId: configuredOwner, role: "Developer"));

        using var response = await client.PostAsJsonAsync(
            "/v1/provider-connections/local-copilot/reauthenticate", new { confirmed = false });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("LocalCopilotReauthenticationUnsupported",
            json.RootElement.GetProperty("code").GetString());

        using var logout = await client.PostAsJsonAsync(
            "/v1/provider-connections/local-copilot/logout", new { confirmed = false });
        using var logoutJson = JsonDocument.Parse(await logout.Content.ReadAsStringAsync());
        using var capabilities = await client.GetAsync("/v1/provider-connections/capabilities");
        var capabilityBody = await capabilities.Content
            .ReadFromJsonAsync<ProviderConnectionCapabilitiesDto>();
        Assert.Equal(HttpStatusCode.Conflict, logout.StatusCode);
        Assert.Equal("LocalCopilotLogoutUnsupported",
            logoutJson.RootElement.GetProperty("code").GetString());
        Assert.NotNull(capabilityBody);
        Assert.True(capabilityBody.LocalCopilotCliEnabled);
    }

    [Fact]
    public async Task GitHubCallbackRequestLogsExcludeCodeAndStateQueryValues()
    {
        const string codeMarker = "oauth-code-must-not-be-logged";
        const string stateMarker = "oauth-state-must-not-be-logged";
        var logs = new CapturingLoggerProvider();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                    .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                    .UseSetting("DataProtection:KeysPath",
                        Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-oauth-logging-dp"))
                    .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                    .UseSetting("Providers:GitHub:Enabled", "false")
                    .UseSetting("ProviderConnectionFeatures:GitHubCopilotOAuthEnabled", "false");
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v1/provider-connections/github/oauth/callback?code={codeMarker}&state={stateMarker}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(logs.Messages, message => message.Contains(
            "/v1/provider-connections/github/oauth/callback", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(codeMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(stateMarker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "configured-test-secret", "ClientId")]
    [InlineData("configured-client-id", "", "ClientSecret")]
    public void EnabledGitHubOAuthWithMissingCredentialFailsStartupValidation(
        string clientId,
        string clientSecret,
        string missingSetting)
    {
        const string callback =
            "http://localhost:8080/api/integrations/v1/provider-connections/github/oauth/callback";
        using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-oauth-invalid-dp"))
                .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                .UseSetting("Providers:GitHub:Enabled", "true")
                .UseSetting("Providers:GitHub:ClientId", clientId)
                .UseSetting("Providers:GitHub:ClientSecret", clientSecret)
                .UseSetting("Providers:GitHub:CallbackUrl", callback));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var diagnostic = exception.ToString();

        Assert.Contains($"Providers:GitHub:{missingSetting}", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("configured-test-secret", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownNumericProviderReturnsValidationProblemBeforeDatabaseAccess()
    {
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-validation-dp"))
                .UseSetting("InternalGrpc:ApiKey", "integration-test-internal-grpc-key-32-chars"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(role: "Developer"));

        using var response = await client.PostAsJsonAsync("/v1/provider-connections/", new
        {
            provider = 99,
            displayName = "Invalid provider",
            apiKey = "must-never-reach-protection-or-database",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DbUpdateException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassicGitHubPatReturnsTypedValidationProblemWithoutLoggingOrDatabaseAccess()
    {
        const string rejectedPat = "ghp_classic-secret-must-not-be-logged";
        var logs = new CapturingLoggerProvider();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:IntegrationsDatabase",
                        "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                    .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                    .UseSetting("DataProtection:KeysPath",
                        Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-pat-validation-dp"))
                    .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                    .UseSetting("Providers:GitHub:Enabled", "false")
                    .UseSetting("ProviderConnectionFeatures:GitHubCopilotPatEnabled", "true");
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(role: "Developer"));

        using var response = await client.PutAsJsonAsync(
            "/v1/provider-connections/github/pat",
            new { personalAccessToken = rejectedPat, expectedGitHubLogin = "expected-user" });
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UnsupportedGitHubCredentialType",
            json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(rejectedPat, body, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages,
            message => message.Contains(rejectedPat, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenericGitHubCreateCannotBypassValidatedConnectionFlows()
    {
        const string rejectedPat = "ghp_classic-secret-must-not-be-logged";
        var logs = new CapturingLoggerProvider();
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:IntegrationsDatabase",
                        "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                    .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                    .UseSetting("DataProtection:KeysPath",
                        Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-pat-bypass-dp"))
                    .UseSetting("InternalGrpc:ApiKey", WorkerKey)
                    .UseSetting("Providers:GitHub:Enabled", "false");
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenFactory.Issue(role: "Developer"));

        using var response = await client.PostAsJsonAsync("/v1/provider-connections/", new
        {
            provider = "GitHubCopilot",
            displayName = "Bypass attempt",
            apiKey = rejectedPat,
        });
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("GitHubConnectionFlowRequired",
            json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(rejectedPat, body, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages,
            message => message.Contains(rejectedPat, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedPersistedRouteReturnsGrpcInvalidArgumentWithoutDatabaseOrProviderAccess()
    {
        await using var factory = new WebApplicationFactory<IntegrationsApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:IntegrationsDatabase",
                    "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), "aicontrolcenter-integrations-route-validation-dp"))
                .UseSetting("InternalGrpc:ApiKey", WorkerKey));
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var client = new AiProviderExecution.AiProviderExecutionClient(channel);

        var exception = await Assert.ThrowsAsync<RpcException>(() => client.ResolveRoutesAsync(
            new ResolveAiProviderRoutesRequest
            {
                OwnerUserId = Guid.NewGuid().ToString(),
                LogicalRoutesJson = "[{\"modelId\":\"deterministic-v1\",\"priority\":1}]",
            }, new Metadata { { "x-worker-api-key", WorkerKey } }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.DoesNotContain("modelId", exception.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidArgumentFromRouteGrpcMapsToControlledRestBadRequest()
    {
        var resolver = new AiProviderRouteResolver(
            new AiProviderExecution.AiProviderExecutionClient(new InvalidRouteCallInvoker()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerGrpc:ApiKey"] = WorkerKey,
            }).Build());

        var validation = await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(
            Guid.NewGuid(), "[{\"provider\":\"DeterministicTest\"}]", CancellationToken.None));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new OrchestratorExceptionHandler(NullLogger<OrchestratorExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, validation, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private sealed class InvalidRouteCallInvoker : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => new(
                Task.FromException<TResponse>(new RpcException(
                    new Status(StatusCode.InvalidArgument, "Persisted route is invalid."))),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options,
            TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options) => throw new NotSupportedException();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }
        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
