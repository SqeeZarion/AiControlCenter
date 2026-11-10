using System.Net;
using System.Text;
using System.Text.Json;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class ProviderAdapterTests
{
    [Fact]
    public async Task OpenAiResponseMapsUsageRequestIdModelAndEstimatedCost()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/responses" => Response(HttpStatusCode.OK,
                "{\"model\":\"gpt-5-mini\",\"output_text\":\"done\",\"usage\":{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":25},\"output_tokens\":20}}",
                "request-42"),
            _ => Response(HttpStatusCode.OK, """{"data":[{"id":"gpt-5-mini"}]}"""),
        });
        var provider = new OpenAiProvider(new HttpClient(handler) { BaseAddress = new Uri("https://stub.invalid/") });

        var result = await provider.ExecuteAsync(Request("gpt-5-mini"), "secret-not-logged", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("request-42", result.ProviderRequestId);
        Assert.Equal("gpt-5-mini", result.ActualModel);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(25, result.CachedInputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(ProviderMetricSource.ProviderReported, result.UsageSource);
        Assert.Equal(ProviderMetricSource.Estimated, result.CostSource);
        Assert.Equal(0.000065m, result.EstimatedCostUsd);
    }

    [Fact]
    public async Task OpenAiRequestContainsBoundedMaxOutputTokens()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(HttpStatusCode.OK,
                "{\"model\":\"gpt-5-mini\",\"output_text\":\"done\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
        });
        var provider = new OpenAiProvider(new HttpClient(handler) { BaseAddress = new Uri("https://stub.invalid/") });

        await provider.ExecuteAsync(Request("gpt-5-mini") with { MaxOutputTokens = 321 }, "secret", CancellationToken.None);

        using var payload = JsonDocument.Parse(body!);
        Assert.Equal(321, payload.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderFailureKind.Authentication, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderFailureKind.RateLimited, true)]
    [InlineData(HttpStatusCode.PaymentRequired, ProviderFailureKind.InsufficientFunds, true)]
    public async Task OpenAiErrorsHaveStrictClassification(HttpStatusCode status,
        ProviderFailureKind expected, bool fallback)
    {
        var provider = new OpenAiProvider(new HttpClient(new StubHandler(_ => Response(status, "{}")))
        { BaseAddress = new Uri("https://stub.invalid/") });

        var result = await provider.ExecuteAsync(Request("gpt-5"), "secret", CancellationToken.None);

        Assert.Equal(expected, result.FailureKind);
        Assert.Equal(fallback, ProviderFallbackPolicy.CanFallback(result.FailureKind));
    }

    [Fact]
    public async Task CopilotAdapterUsesInjectedRuntimeAndClassifiesAuthenticationFailure()
    {
        var runtime = new StubCopilotRuntime { Failure = new UnauthorizedAccessException() };
        var provider = new GitHubCopilotProvider(new HttpClient(new StubHandler(_ => Response(HttpStatusCode.OK, "{}"))),
            Options.Create(new GitHubOAuthOptions()), TimeProvider.System, runtime,
            NullLogger<GitHubCopilotProvider>.Instance);

        var result = await provider.ExecuteAsync(Request("copilot-model"), "user-oauth-token", CancellationToken.None);

        Assert.Equal("user-oauth-token", runtime.LastAccessToken);
        Assert.Equal(ProviderFailureKind.Authentication, result.FailureKind);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
    }

    [Theory]
    [MemberData(nameof(CopilotNonFallbackFailures))]
    public async Task CopilotUnknownOrAmbiguousFailuresNeverAllowFallback(
        Exception exception,
        ProviderFailureKind expected)
    {
        var runtime = new StubCopilotRuntime { Failure = exception };
        var provider = new GitHubCopilotProvider(new HttpClient(new StubHandler(_ => Response(HttpStatusCode.OK, "{}"))),
            Options.Create(new GitHubOAuthOptions()), TimeProvider.System, runtime,
            NullLogger<GitHubCopilotProvider>.Instance);

        var result = await provider.ExecuteAsync(Request("copilot-model"), "user-oauth-token", CancellationToken.None);

        Assert.Equal(expected, result.FailureKind);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
    }

    public static TheoryData<Exception, ProviderFailureKind> CopilotNonFallbackFailures => new()
    {
        { new InvalidOperationException("malformed SDK response"), ProviderFailureKind.Configuration },
        { new OperationCanceledException("provider timeout"), ProviderFailureKind.AmbiguousTimeout },
        { new HttpRequestException("transport outcome unknown"), ProviderFailureKind.AmbiguousTimeout },
        { new FormatException("malformed SDK response"), ProviderFailureKind.Configuration },
    };

    [Fact]
    public async Task CopilotValidationUsesCurrentUsersOAuthTokenAndReturnsCatalog()
    {
        var runtime = new StubCopilotRuntime();
        var provider = CreateCopilotProvider(runtime);

        var result = await provider.ValidateConnectionAsync("current-user-oauth-token", CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.Valid, result.Kind);
        Assert.Single(result.Models!);
        Assert.Equal("current-user-oauth-token", runtime.LastAccessToken);
        Assert.Equal("12345", result.ExternalAccountId);
        Assert.Equal("student-user", result.ExternalAccountLogin);
    }

    [Fact]
    public void CopilotClientUsesExplicitUserTokenWithoutAmbientLoginFallback()
    {
        var options = CopilotClientConfiguration.CreateOptions("current-user-oauth-token");

        Assert.Equal("current-user-oauth-token", options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
    }

    [Fact]
    public async Task CopilotAdapterAcceptsFineGrainedPatAndPassesItExplicitlyToTheSdkRuntime()
    {
        const string pat = "github_pat_write_only_test_value_123456789";
        var runtime = new StubCopilotRuntime();
        var provider = CreateCopilotProvider(runtime);

        var result = await provider.ValidateConnectionAsync(pat, CancellationToken.None);
        var options = CopilotClientConfiguration.CreateOptions(pat);

        Assert.Equal(ProviderCredentialValidationKind.Valid, result.Kind);
        Assert.Equal(pat, runtime.LastAccessToken);
        Assert.Equal(pat, options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderCredentialValidationKind.RequiresReconnect)]
    [InlineData(HttpStatusCode.Forbidden, ProviderCredentialValidationKind.UnknownFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderCredentialValidationKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderCredentialValidationKind.TemporarilyUnavailable)]
    public async Task CopilotValidationClassifiesProviderStatusWithoutLeakingCredential(
        HttpStatusCode status,
        ProviderCredentialValidationKind expected)
    {
        var runtime = new StubCopilotRuntime
        {
            ListModelsFailure = new HttpRequestException("safe test failure", null, status),
        };

        var result = await CreateCopilotProvider(runtime)
            .ValidateConnectionAsync("current-user-oauth-token", CancellationToken.None);

        Assert.Equal(expected, result.Kind);
        Assert.Equal("current-user-oauth-token", runtime.LastAccessToken);
    }

    [Fact]
    public async Task CopilotValidationClassifiesTimeoutAsTemporary()
    {
        var runtime = new StubCopilotRuntime { ListModelsFailure = new TimeoutException() };

        var result = await CreateCopilotProvider(runtime)
            .ValidateConnectionAsync("current-user-oauth-token", CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.TemporarilyUnavailable, result.Kind);
    }

    [Fact]
    public async Task CopilotValidationKeepsConnectionValidWhenQuotaIsUnavailable()
    {
        var runtime = new StubCopilotRuntime { QuotaFailure = new TimeoutException() };

        var result = await CreateCopilotProvider(runtime)
            .ValidateConnectionAsync("current-user-oauth-token", CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.Valid, result.Kind);
        Assert.Equal(ProviderMetricSource.Unavailable, result.QuotaSource);
        Assert.Equal("Unavailable", result.QuotaState);
    }

    [Fact]
    public async Task CopilotValidationClassifiesEmptyModelCatalogSeparately()
    {
        var runtime = new StubCopilotRuntime { Models = [] };

        var result = await CreateCopilotProvider(runtime)
            .ValidateConnectionAsync("current-user-oauth-token", CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.ModelCatalogUnavailable, result.Kind);
    }

    [Theory]
    [MemberData(nameof(ForbiddenCopilotResponses))]
    public async Task CopilotValidationClassifiesForbiddenOAuthExchange(
        string responseBody,
        ProviderCredentialValidationKind expected)
    {
        var runtime = new StubCopilotRuntime { Models = [] };
        var provider = new GitHubCopilotProvider(
            new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/user" => Response(HttpStatusCode.OK, "{\"id\":12345,\"login\":\"student-user\"}"),
                "/copilot_internal/v2/token" => Response(HttpStatusCode.Forbidden, responseBody,
                    "github-request-id", "repo, user:email", "copilot_requests"),
                _ => Response(HttpStatusCode.NotFound, "{}"),
            })),
            Options.Create(new GitHubOAuthOptions { ClientId = "test-oauth-client" }), TimeProvider.System, runtime,
            NullLogger<GitHubCopilotProvider>.Instance);

        var result = await provider.ValidateConnectionAsync(
            "current-user-oauth-token", CancellationToken.None);

        Assert.Equal(expected, result.Kind);
        Assert.Equal("12345", result.ExternalAccountId);
        Assert.Equal("student-user", result.ExternalAccountLogin);
    }

    public static TheoryData<string, ProviderCredentialValidationKind> ForbiddenCopilotResponses => new()
    {
        { "{\"message\":\"Copilot is disabled by policy\"}", ProviderCredentialValidationKind.PolicyBlocked },
        { "{\"message\":\"OAuth App is not supported for Copilot\"}", ProviderCredentialValidationKind.OAuthAppUnsupported },
        { "{\"message\":\"Copilot subscription is not available\"}", ProviderCredentialValidationKind.NotEntitled },
        { "{\"message\":\"Resource not accessible by integration\"}", ProviderCredentialValidationKind.InsufficientPermissions },
    };

    [Fact]
    public async Task CopilotTokenExchangeNotFoundIsARejectedCredentialRatherThanATemporaryOutage()
    {
        var runtime = new StubCopilotRuntime { Models = [] };
        var provider = new GitHubCopilotProvider(
            new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/user" => Response(HttpStatusCode.OK, "{\"id\":12345,\"login\":\"student-user\"}"),
                "/copilot_internal/v2/token" => Response(HttpStatusCode.NotFound,
                    "{\"message\":\"Not Found\"}", "github-request-id"),
                _ => Response(HttpStatusCode.NotFound, "{}"),
            })),
            Options.Create(new GitHubOAuthOptions()), TimeProvider.System, runtime,
            NullLogger<GitHubCopilotProvider>.Instance);

        var result = await provider.ValidateConnectionAsync(
            "github_pat_fictional_opaque_value", CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.InsufficientPermissions, result.Kind);
        Assert.Equal("12345", result.ExternalAccountId);
        Assert.Equal("student-user", result.ExternalAccountLogin);
    }

    [Fact]
    public async Task CopilotForbiddenDiagnosticLogsOnlySanitizedMetadata()
    {
        const string accessToken = "gho_token-value-must-not-be-logged";
        const string clientId = "client-id-must-not-be-logged";
        const string responseSecret = "response-secret-must-not-be-logged";
        var logger = new CapturingLogger<GitHubCopilotProvider>();
        var provider = new GitHubCopilotProvider(
            new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/user" => Response(HttpStatusCode.OK, "{\"id\":12345,\"login\":\"student-user\"}"),
                "/copilot_internal/v2/token" => Response(HttpStatusCode.Forbidden,
                    "{\"message\":\"Resource not accessible by integration\",\"secret\":\""
                    + responseSecret + "\"}",
                    "ABCD:1234", "read:user", "copilot_requests"),
                _ => Response(HttpStatusCode.NotFound, "{}"),
            })),
            Options.Create(new GitHubOAuthOptions { ClientId = clientId }), TimeProvider.System,
            new StubCopilotRuntime { Models = [] }, logger);

        await provider.ValidateConnectionAsync(accessToken, CancellationToken.None);

        var diagnostic = Assert.Single(logger.Messages, message =>
            message.Contains("GitHub Copilot diagnostic", StringComparison.Ordinal));
        Assert.Contains("operation copilot.tokenExchange", diagnostic, StringComparison.Ordinal);
        Assert.Contains("endpoint api.github.com/copilot_internal/v2/token", diagnostic, StringComparison.Ordinal);
        Assert.Contains("status 403", diagnostic, StringComparison.Ordinal);
        Assert.Contains("resource_not_accessible_by_integration", diagnostic, StringComparison.Ordinal);
        Assert.Contains("request id ABCD:1234", diagnostic, StringComparison.Ordinal);
        Assert.Contains("token category gho_", diagnostic, StringComparison.Ordinal);
        Assert.Contains("OAuth scopes read:user", diagnostic, StringComparison.Ordinal);
        Assert.Contains("accepted OAuth scopes copilot_requests", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(clientId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(responseSecret, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotClassifierRequiresExplicitEntitlementSignal()
    {
        var genericForbidden = CopilotValidationFailureClassifier.Classify(
            new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden));
        var entitlement = CopilotValidationFailureClassifier.Classify(
            new InvalidOperationException("copilot_not_entitled"));

        Assert.Equal(ProviderCredentialValidationKind.UnknownFailure, genericForbidden.Kind);
        Assert.Equal(ProviderCredentialValidationKind.NotEntitled, entitlement.Kind);
    }

    [Theory]
    [InlineData("{}", "Unavailable")]
    [InlineData("{\"quotaSnapshots\":{}}", "Unavailable")]
    [InlineData("{\"quotaSnapshots\":{\"premium\":{\"remainingPercentage\":0}}}", "Exhausted")]
    [InlineData("{\"quotaSnapshots\":{\"student\":{\"isUnlimitedEntitlement\":true}}}", "Available")]
    [InlineData("{\"quotaSnapshots\":{\"new-credit-key\":{\"futureField\":200}}}", "Unavailable")]
    public void QuotaInspectionIsConservativeAndSchemaTolerant(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, CopilotQuotaInspector.Classify(document.RootElement));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderCredentialValidationKind.RequiresReconnect)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderCredentialValidationKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderCredentialValidationKind.TemporarilyUnavailable)]
    public async Task GitHubIdentityFailureIsTyped(HttpStatusCode status,
        ProviderCredentialValidationKind expected)
    {
        var runtime = new StubCopilotRuntime();
        var provider = new GitHubCopilotProvider(
            new HttpClient(new StubHandler(_ => Response(status, "{}"))),
            Options.Create(new GitHubOAuthOptions()), TimeProvider.System, runtime,
            NullLogger<GitHubCopilotProvider>.Instance);

        var result = await provider.ValidateConnectionAsync("current-user-oauth-token", CancellationToken.None);

        Assert.Equal(expected, result.Kind);
        Assert.Null(runtime.LastAccessToken);
    }

    private static GitHubCopilotProvider CreateCopilotProvider(StubCopilotRuntime runtime) =>
        new(new HttpClient(new StubHandler(_ => Response(HttpStatusCode.OK,
                "{\"id\":12345,\"login\":\"student-user\"}"))),
            Options.Create(new GitHubOAuthOptions()), TimeProvider.System, runtime,
            NullLogger<GitHubCopilotProvider>.Instance);

    private static AiExecutionRequest Request(string model) => new(Guid.NewGuid(), Guid.NewGuid(), model,
        "system", "input", [], null, "idempotency-1");
    private static HttpResponseMessage Response(HttpStatusCode status, string json, string? requestId = null,
        string? oauthScopes = null, string? acceptedOauthScopes = null)
    {
        var response = new HttpResponseMessage(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-request-id", requestId);
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", requestId);
        }
        if (oauthScopes is not null) response.Headers.TryAddWithoutValidation("X-OAuth-Scopes", oauthScopes);
        if (acceptedOauthScopes is not null)
            response.Headers.TryAddWithoutValidation("X-Accepted-OAuth-Scopes", acceptedOauthScopes);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class StubCopilotRuntime : ICopilotRuntime
    {
        public Exception? Failure { get; init; }
        public Exception? ListModelsFailure { get; init; }
        public Exception? QuotaFailure { get; init; }
        public IReadOnlyCollection<ProviderModelDto> Models { get; init; } =
            [new("copilot-model", "Copilot", null, true, true, null, null)];
        public string? LastAccessToken { get; private set; }
        public Task<CopilotRuntimeInspection> InspectAsync(string accessToken, CancellationToken token)
        {
            LastAccessToken = accessToken;
            if (ListModelsFailure is not null)
                return Task.FromException<CopilotRuntimeInspection>(ListModelsFailure);
            return Task.FromResult(new CopilotRuntimeInspection(Models,
                QuotaFailure is null ? ProviderMetricSource.ProviderReported : ProviderMetricSource.Unavailable,
                QuotaFailure is null ? "Available" : "Unavailable", QuotaFailure));
        }
        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string accessToken, CancellationToken token)
        {
            LastAccessToken = accessToken;
            return ListModelsFailure is null
                ? Task.FromResult(Models)
                : Task.FromException<IReadOnlyCollection<ProviderModelDto>>(ListModelsFailure);
        }
        public Task<CopilotRuntimeResult> ExecuteAsync(string accessToken, string model, string systemPrompt,
            string input, IReadOnlyCollection<ToolDefinition> tools,
            IReadOnlyCollection<ToolInvocationResult> priorResults, CancellationToken token)
        {
            LastAccessToken = accessToken;
            return Failure is null
                ? Task.FromResult(new CopilotRuntimeResult("done", []))
                : Task.FromException<CopilotRuntimeResult>(Failure);
        }
    }
}
