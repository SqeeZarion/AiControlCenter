using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

internal sealed partial class GitHubCopilotProvider(HttpClient httpClient, IOptions<GitHubOAuthOptions> configuredOptions,
    TimeProvider timeProvider, ICopilotRuntime runtime, ILogger<GitHubCopilotProvider> logger) : IAiProvider
{
    private readonly GitHubOAuthOptions options = configuredOptions.Value;

    public AiProviderKind Kind => AiProviderKind.GitHubCopilot;

    public async Task<bool> ValidateAsync(string? credential, CancellationToken token)
        => (await ValidateConnectionAsync(credential, token)).Kind == ProviderCredentialValidationKind.Valid;

    public async Task<ProviderCredentialValidationResult> ValidateConnectionAsync(
        string? credential,
        CancellationToken token)
    {
        var accessToken = GitHubOAuthCredential.Parse(credential).AccessToken;
        GitHubExternalIdentity identity;
        try
        {
            identity = await GetIdentityAsync(accessToken, token);
            if (logger.IsEnabled(LogLevel.Information))
            {
                var tokenCategory = TokenCategory(accessToken);
                var correlationId = CorrelationId();
                LogIdentityValidated(logger, identity.Id, identity.Login, tokenCategory, correlationId);
            }
        }
        catch (GitHubIdentityValidationException exception)
        {
            LogValidationFailure(logger, "github.user", exception.Kind, exception.GetType().Name,
                exception.ErrorCode, exception.StatusCode, CorrelationId());
            return new(exception.Kind);
        }

        try
        {
            var inspection = await runtime.InspectAsync(accessToken, token);
            if (logger.IsEnabled(LogLevel.Information))
            {
                var correlationId = CorrelationId();
                LogValidationOperationSucceeded(logger, "models.list", inspection.Models.Count, correlationId);
                LogRuntimeAuthentication(logger, inspection.IsAuthenticated, inspection.AuthType,
                    inspection.CopilotPlan, inspection.ChatEnabled, correlationId);
            }
            if (inspection.QuotaFailure is not null)
            {
                var quotaFailure = CopilotValidationFailureClassifier.Classify(inspection.QuotaFailure);
                LogValidationFailure(logger, "account.getQuota", quotaFailure.Kind,
                    quotaFailure.ExceptionType, quotaFailure.ErrorCode, quotaFailure.UpstreamStatus,
                    CorrelationId());
            }
            if (inspection.Models.Count == 0)
            {
                var accessProbe = await ProbeCopilotAccessAsync(accessToken, token);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var correlationId = CorrelationId();
                    LogCopilotAccessProbe(logger, accessProbe.Operation, accessProbe.EndpointHost,
                        accessProbe.EndpointPath, accessProbe.StatusCode, accessProbe.ResultCode,
                        accessProbe.SafeMessage, accessProbe.RequestId, accessProbe.TokenCategory,
                        accessProbe.OAuthScopes, accessProbe.AcceptedOAuthScopes,
                        accessProbe.ClientIdFingerprint, correlationId);
                }
                LogValidationFailure(logger, accessProbe.Operation, accessProbe.FailureKind,
                    "None", accessProbe.ResultCode, accessProbe.StatusCode, CorrelationId());
                return new(accessProbe.FailureKind,
                    ExternalAccountId: identity.Id, ExternalAccountLogin: identity.Login);
            }

            var kind = string.Equals(inspection.QuotaState, "Exhausted", StringComparison.Ordinal)
                ? ProviderCredentialValidationKind.QuotaExhausted
                : ProviderCredentialValidationKind.Valid;
            return new(kind, inspection.Models, identity.Id, identity.Login,
                inspection.QuotaSource, inspection.QuotaState);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var operation = exception is CopilotRuntimeOperationException runtimeFailure
                ? runtimeFailure.Operation
                : "models.list";
            var cause = exception is CopilotRuntimeOperationException { InnerException: { } inner }
                ? inner
                : exception;
            var failure = CopilotValidationFailureClassifier.Classify(cause);
            LogValidationFailure(logger, operation, failure.Kind, failure.ExceptionType,
                failure.ErrorCode, failure.UpstreamStatus, CorrelationId());
            return new(failure.Kind, ExternalAccountId: identity.Id, ExternalAccountLogin: identity.Login);
        }
    }

    public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken token) =>
        runtime.ListModelsAsync(GitHubOAuthCredential.Parse(credential).AccessToken, token);

    private async Task<GitHubExternalIdentity> GetIdentityAsync(string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("AiControlCenter/1.0");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new GitHubIdentityValidationException(ProviderCredentialValidationKind.RequiresReconnect,
                (int)response.StatusCode, "github_identity_unauthorized");
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.Forbidden
                && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                && remaining.Contains("0", StringComparer.Ordinal))
            throw new GitHubIdentityValidationException(ProviderCredentialValidationKind.RateLimited,
                (int)response.StatusCode, "github_identity_rate_limited");
        if ((int)response.StatusCode >= 500)
            throw new GitHubIdentityValidationException(ProviderCredentialValidationKind.TemporarilyUnavailable,
                (int)response.StatusCode, "github_identity_unavailable");
        if (!response.IsSuccessStatusCode)
            throw new GitHubIdentityValidationException(ProviderCredentialValidationKind.UnknownFailure,
                (int)response.StatusCode, "github_identity_rejected");

        var payload = await response.Content.ReadFromJsonAsync<GitHubIdentityResponse>(cancellationToken: token);
        if (payload is null || payload.Id <= 0 || string.IsNullOrWhiteSpace(payload.Login))
            throw new GitHubIdentityValidationException(ProviderCredentialValidationKind.UnknownFailure,
                (int)response.StatusCode, "github_identity_invalid_response");
        return new(payload.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), payload.Login);
    }

    private async Task<CopilotAccessProbe> ProbeCopilotAccessAsync(
        string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/copilot_internal/v2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AiControlCenter/1.0");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var statusCode = (int)response.StatusCode;
        var responseMessage = await ReadSafeResponseMessageAsync(response, token);
        var failureKind = ClassifyAccessProbe(response.StatusCode, responseMessage);
        var resultCode = ClassifyAccessProbeCode(response.StatusCode, responseMessage);
        return new(statusCode, resultCode, SafeResponseMessage(resultCode), failureKind,
            "copilot.tokenExchange", "api.github.com", "/copilot_internal/v2/token",
            SafeHeader(response, "X-GitHub-Request-Id", 128),
            TokenCategory(accessToken),
            SafeHeader(response, "X-OAuth-Scopes", 256),
            SafeHeader(response, "X-Accepted-OAuth-Scopes", 256),
            ClientIdFingerprint(options.ClientId));
    }

    private static ProviderCredentialValidationKind ClassifyAccessProbe(
        HttpStatusCode statusCode,
        string? responseMessage)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
            return ProviderCredentialValidationKind.RequiresReconnect;
        if (statusCode == HttpStatusCode.TooManyRequests)
            return ProviderCredentialValidationKind.RateLimited;
        if ((int)statusCode >= 500)
            return ProviderCredentialValidationKind.TemporarilyUnavailable;
        if (statusCode == HttpStatusCode.OK)
            return ProviderCredentialValidationKind.ModelCatalogUnavailable;
        if (statusCode == HttpStatusCode.NotFound)
            return ProviderCredentialValidationKind.InsufficientPermissions;
        if (statusCode != HttpStatusCode.Forbidden)
            return ProviderCredentialValidationKind.UnknownFailure;

        if (ContainsAny(responseMessage, "not entitled", "subscription", "license"))
            return ProviderCredentialValidationKind.NotEntitled;
        if (ContainsAny(responseMessage, "policy", "disabled", "blocked"))
            return ProviderCredentialValidationKind.PolicyBlocked;
        if (ContainsAny(responseMessage, "oauth app", "oauth client", "client is not supported"))
            return ProviderCredentialValidationKind.OAuthAppUnsupported;
        return ProviderCredentialValidationKind.InsufficientPermissions;
    }

    private static string ClassifyAccessProbeCode(HttpStatusCode statusCode, string? responseMessage)
    {
        if (ContainsAny(responseMessage, "resource not accessible by integration"))
            return "resource_not_accessible_by_integration";
        if (ContainsAny(responseMessage, "resource not accessible by personal access token"))
            return "resource_not_accessible_by_personal_access_token";
        if (ContainsAny(responseMessage, "not entitled", "subscription", "license"))
            return "copilot_not_entitled";
        if (ContainsAny(responseMessage, "policy", "disabled", "blocked"))
            return "copilot_policy_blocked";
        if (ContainsAny(responseMessage, "oauth app", "oauth client", "client is not supported"))
            return "copilot_oauth_app_unsupported";
        return statusCode switch
        {
            HttpStatusCode.OK => "copilot_model_catalog_empty",
            HttpStatusCode.Unauthorized => "copilot_bearer_exchange_unauthorized",
            HttpStatusCode.Forbidden => "copilot_bearer_exchange_forbidden",
            HttpStatusCode.NotFound => "copilot_bearer_exchange_not_found",
            HttpStatusCode.TooManyRequests => "copilot_bearer_exchange_rate_limited",
            >= HttpStatusCode.InternalServerError => "copilot_bearer_exchange_unavailable",
            _ => "copilot_bearer_exchange_rejected",
        };
    }

    private static string SafeResponseMessage(string resultCode) => resultCode switch
    {
        "resource_not_accessible_by_integration" => "Resource not accessible by integration.",
        "resource_not_accessible_by_personal_access_token" =>
            "Resource not accessible by personal access token.",
        "copilot_not_entitled" => "Copilot entitlement is unavailable for this account.",
        "copilot_policy_blocked" => "Copilot access is blocked by policy.",
        "copilot_oauth_app_unsupported" => "GitHub rejected this OAuth application for Copilot access.",
        "copilot_bearer_exchange_unauthorized" => "GitHub rejected the user credential.",
        "copilot_bearer_exchange_rate_limited" => "GitHub rate limited the Copilot access check.",
        "copilot_bearer_exchange_unavailable" => "GitHub Copilot access check is temporarily unavailable.",
        "copilot_model_catalog_empty" => "GitHub Copilot returned an empty model catalog.",
        _ => "GitHub rejected the Copilot access check.",
    };

    private static async Task<string?> ReadSafeResponseMessageAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        const int maximumResponseBytes = 8192;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[maximumResponseBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token);
            if (read == 0) break;
            length += read;
        }
        if (length == 0 || length > maximumResponseBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            foreach (var propertyName in new[] { "message", "error_description", "error" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String)
                    return property.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static string SafeHeader(HttpResponseMessage response, string name, int maximumLength)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return "unavailable";
        var value = string.Join(",", values);
        if (value.Length is 0 || value.Length > maximumLength) return "unavailable";
        return value.All(character => char.IsLetterOrDigit(character)
            || character is ' ' or ',' or ':' or '_' or '-' or '.')
            ? value
            : "unavailable";
    }

    private static bool ContainsAny(string? value, params string[] candidates) =>
        !string.IsNullOrWhiteSpace(value)
        && candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string ClientIdFingerprint(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return "not-configured";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..12]
            .ToLowerInvariant();
    }

    public async Task<AiExecutionResult> ExecuteAsync(AiExecutionRequest request, string? credential, CancellationToken token)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var response = await runtime.ExecuteAsync(GitHubOAuthCredential.Parse(credential).AccessToken,
                request.ModelId, request.SystemPrompt, request.Input,
                request.ToolDefinitions ?? [], request.PriorToolResults ?? [], token);
            if (response.ToolCalls.Count > 0)
                return new(false, null, ProviderFailureKind.None, null, null, 0, 0, null, [],
                    request.ModelId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null,
                    ProviderMetricSource.Unavailable, ProviderMetricSource.Unavailable, "ToolCalls",
                    RequestedToolCalls: response.ToolCalls);
            if (string.IsNullOrWhiteSpace(response.Content))
                return new(false, null, ProviderFailureKind.Configuration,
                    "copilot_protocol_invalid", null, 0, 0, null, []);
            if (request.MaxOutputTokens is > 0
                && response.Content.Length > checked(request.MaxOutputTokens.Value * 4L))
                return new(false, null, ProviderFailureKind.Safety,
                    "copilot_output_limit_exceeded", null, 0, 0, null, []);
            return new(true, response.Content, ProviderFailureKind.None, null, null, 0, 0, null, [],
                request.ModelId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null,
                ProviderMetricSource.Unavailable, ProviderMetricSource.Unavailable, "Completed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(false, null, ProviderFailureKind.Cancelled, "cancelled", null, 0, 0, null, []); }
        catch (OperationCanceledException)
        { return new(false, null, ProviderFailureKind.AmbiguousTimeout, "copilot_result_ambiguous", null, 0, 0, null, []); }
        catch (UnauthorizedAccessException)
        { return new(false, null, ProviderFailureKind.Authentication, "copilot_authentication_failed", null, 0, 0, null, []); }
        catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        { return new(false, null, ProviderFailureKind.Authentication, "copilot_authentication_failed", null, 0, 0, null, []); }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        { return new(false, null, ProviderFailureKind.RateLimited, "copilot_rate_limited", null, 0, 0, null, []); }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
        { return new(false, null, ProviderFailureKind.InsufficientFunds, "copilot_insufficient_funds", null, 0, 0, null, []); }
        catch (HttpRequestException exception) when ((int?)exception.StatusCode >= 500)
        { return new(false, null, ProviderFailureKind.TemporarilyUnavailable, "copilot_unavailable", null, 0, 0, null, []); }
        catch (HttpRequestException)
        { return new(false, null, ProviderFailureKind.AmbiguousTimeout, "copilot_result_ambiguous", null, 0, 0, null, []); }
        catch (InvalidOperationException)
        { return new(false, null, ProviderFailureKind.Configuration, "copilot_protocol_invalid", null, 0, 0, null, []); }
        catch (FormatException)
        { return new(false, null, ProviderFailureKind.Configuration, "copilot_protocol_invalid", null, 0, 0, null, []); }
        catch (JsonException)
        { return new(false, null, ProviderFailureKind.Configuration, "copilot_protocol_invalid", null, 0, 0, null, []); }
        catch (Exception)
        { return new(false, null, ProviderFailureKind.Configuration, "copilot_unclassified_failure", null, 0, 0, null, []); }
    }

    public IAsyncEnumerable<AiExecutionChunk> ExecuteStreamingAsync(
        AiExecutionRequest request,
        string? credential,
        CancellationToken token) => AiProviderStreaming.BufferAsync(this, request, credential, token);

    public async Task<string?> RefreshCredentialAsync(string? credential, CancellationToken token)
    {
        var envelope = GitHubOAuthCredential.Parse(credential);
        if (envelope.ExpiresAt is null || envelope.ExpiresAt > timeProvider.GetUtcNow().AddMinutes(2))
            return credential;
        if (string.IsNullOrWhiteSpace(envelope.RefreshToken)
            || envelope.RefreshTokenExpiresAt <= timeProvider.GetUtcNow())
            throw new UnauthorizedAccessException("GitHub OAuth credential expired and cannot be refreshed.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = JsonContent.Create(new
            {
                client_id = options.ClientId,
                client_secret = options.ClientSecret,
                grant_type = "refresh_token",
                refresh_token = envelope.RefreshToken,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, token);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("GitHub OAuth refresh was rejected.");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RefreshTokenResponse>(cancellationToken: token);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            throw new InvalidOperationException("GitHub OAuth refresh did not return an access token.");
        return GitHubOAuthCredential.Serialize(payload.AccessToken,
            payload.RefreshToken ?? envelope.RefreshToken,
            payload.ExpiresIn is > 0 ? timeProvider.GetUtcNow().AddSeconds(payload.ExpiresIn.Value) : null,
            payload.RefreshTokenExpiresIn is > 0
                ? timeProvider.GetUtcNow().AddSeconds(payload.RefreshTokenExpiresIn.Value)
                : envelope.RefreshTokenExpiresAt);
    }

    private sealed record RefreshTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn);

    private static string TokenCategory(string accessToken) => accessToken switch
    {
        var value when value.StartsWith("gho_", StringComparison.Ordinal) => "gho_",
        var value when value.StartsWith("ghu_", StringComparison.Ordinal) => "ghu_",
        var value when value.StartsWith("github_pat_", StringComparison.Ordinal) => "github_pat_",
        _ => "other",
    };

    private static string CorrelationId() => Activity.Current?.TraceId.ToString() ?? "unavailable";

    private sealed record GitHubIdentityResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login);

    [LoggerMessage(EventId = 6200, Level = LogLevel.Warning,
        Message = "GitHub Copilot validation operation {Operation} failed with {FailureKind}; exception {ExceptionType}; provider code {ProviderCode}; upstream status {UpstreamStatus}; correlation {CorrelationId}")]
    private static partial void LogValidationFailure(ILogger logger, string operation,
        ProviderCredentialValidationKind failureKind, string exceptionType, string? providerCode,
        int? upstreamStatus, string correlationId);

    [LoggerMessage(EventId = 6201, Level = LogLevel.Information,
        Message = "GitHub OAuth identity validated for account {ExternalAccountId} ({ExternalAccountLogin}); token category {TokenCategory}; correlation {CorrelationId}")]
    private static partial void LogIdentityValidated(ILogger logger, string externalAccountId,
        string externalAccountLogin, string tokenCategory, string correlationId);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Information,
        Message = "GitHub Copilot validation operation {Operation} completed with {ResultCount} results; correlation {CorrelationId}")]
    private static partial void LogValidationOperationSucceeded(ILogger logger, string operation,
        int resultCount, string correlationId);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Information,
        Message = "GitHub Copilot runtime authentication resolved: authenticated {IsAuthenticated}; auth type {AuthType}; plan {CopilotPlan}; chat enabled {ChatEnabled}; correlation {CorrelationId}")]
    private static partial void LogRuntimeAuthentication(ILogger logger, bool isAuthenticated,
        string authType, string copilotPlan, bool? chatEnabled, string correlationId);

    [LoggerMessage(EventId = 6204, Level = LogLevel.Information,
        Message = "GitHub Copilot diagnostic: operation {Operation}; endpoint {EndpointHost}{EndpointPath}; upstream status {UpstreamStatus}; code {ResultCode}; response {SafeMessage}; request id {RequestId}; token category {TokenCategory}; OAuth scopes {OAuthScopes}; accepted OAuth scopes {AcceptedOAuthScopes}; OAuth client fingerprint {ClientIdFingerprint}; correlation {CorrelationId}")]
    private static partial void LogCopilotAccessProbe(ILogger logger, string operation,
        string endpointHost, string endpointPath, int upstreamStatus, string resultCode,
        string safeMessage, string requestId, string tokenCategory, string oAuthScopes,
        string acceptedOAuthScopes, string clientIdFingerprint, string correlationId);
}

internal static class CopilotValidationFailureClassifier
{
    internal static CopilotValidationFailure Classify(Exception exception)
    {
        foreach (var current in Enumerate(exception))
        {
            if (current is HttpRequestException http && http.StatusCode is { } status)
                return FromStatusCode((int)status, current.GetType().Name);
            if (current is RemoteRpcException remote)
            {
                var providerCode = remote.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryReadSafeProviderCode(remote.ErrorData, out var safeCode)) providerCode = safeCode;
                if (IsEntitlementCode(providerCode))
                    return new(ProviderCredentialValidationKind.NotEntitled, providerCode,
                        TryReadStatusCode(remote.ErrorData, out var entitlementStatus) ? entitlementStatus : null,
                        current.GetType().Name);
                if (IsQuotaCode(providerCode))
                    return new(ProviderCredentialValidationKind.QuotaExhausted, providerCode,
                        TryReadStatusCode(remote.ErrorData, out var quotaStatus) ? quotaStatus : null,
                        current.GetType().Name);
                if (TryReadStatusCode(remote.ErrorData, out var remoteStatus))
                    return FromStatusCode(remoteStatus, current.GetType().Name, providerCode);
            }
            if (current is UnauthorizedAccessException)
                return new(ProviderCredentialValidationKind.RequiresReconnect, "unauthorized", null,
                    current.GetType().Name);
        }

        var diagnostic = string.Join(' ', Enumerate(exception).Select(current => current.Message));
        if (ContainsAny(diagnostic, "401", "invalid token", "bad credentials", "token revoked"))
            return new(ProviderCredentialValidationKind.RequiresReconnect, "unauthorized", 401,
                exception.GetType().Name);
        if (ContainsAny(diagnostic, "not_entitled", "copilot_not_entitled", "subscription_required",
            "not authorized to use this copilot feature"))
            return new(ProviderCredentialValidationKind.NotEntitled, "copilot_not_entitled", 403,
                exception.GetType().Name);
        if (ContainsAny(diagnostic, "quota_exhausted", "credits_exhausted", "insufficient_quota"))
            return new(ProviderCredentialValidationKind.QuotaExhausted, "quota_exhausted", null,
                exception.GetType().Name);
        if (ContainsAny(diagnostic, "429", "rate limit", "too many requests"))
            return new(ProviderCredentialValidationKind.RateLimited, "rate_limited", 429,
                exception.GetType().Name);
        if (Enumerate(exception).Any(current => current is TimeoutException
                or OperationCanceledException or IOException or HttpRequestException))
            return new(ProviderCredentialValidationKind.TemporarilyUnavailable, "transport_unavailable", null,
                exception.GetType().Name);
        if (ContainsAny(diagnostic, "500", "502", "503", "504", "temporarily unavailable"))
            return new(ProviderCredentialValidationKind.TemporarilyUnavailable, "provider_unavailable", null,
                exception.GetType().Name);
        return new(ProviderCredentialValidationKind.UnknownFailure, null, null, exception.GetType().Name);
    }

    private static IEnumerable<Exception> Enumerate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
            yield return current;
    }

    private static CopilotValidationFailure FromStatusCode(int statusCode, string exceptionType,
        string? providerCode = null) => statusCode switch
        {
            401 => new(ProviderCredentialValidationKind.RequiresReconnect, providerCode ?? "unauthorized", 401,
                exceptionType),
            429 => new(ProviderCredentialValidationKind.RateLimited, providerCode ?? "rate_limited", 429,
                exceptionType),
            >= 500 and <= 599 => new(ProviderCredentialValidationKind.TemporarilyUnavailable,
                providerCode ?? "provider_unavailable", statusCode, exceptionType),
            _ => new(ProviderCredentialValidationKind.UnknownFailure, providerCode, statusCode, exceptionType),
        };

    private static bool IsEntitlementCode(string? value) => value is not null &&
        new[] { "not_entitled", "copilot_not_entitled", "subscription_required", "policy_denied" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsQuotaCode(string? value) => value is not null &&
        new[] { "quota_exhausted", "credits_exhausted", "insufficient_quota" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool TryReadStatusCode(JsonElement? data, out int statusCode)
    {
        statusCode = 0;
        return data is { } element && TryReadStatusCode(element, 0, out statusCode);
    }

    private static bool TryReadStatusCode(JsonElement element, int depth, out int statusCode)
    {
        statusCode = 0;
        if (depth > 4) return false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "status" or "statusCode" or "httpStatus"
                    && TryReadInteger(property.Value, out statusCode))
                    return true;
                if (TryReadStatusCode(property.Value, depth + 1, out statusCode))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryReadStatusCode(item, depth + 1, out statusCode)) return true;
        }
        return false;
    }

    private static bool TryReadInteger(JsonElement value, out int result) =>
        value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out result)
            : int.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null, out result);

    private static bool TryReadSafeProviderCode(JsonElement? data, out string? providerCode)
    {
        providerCode = null;
        if (data is not { ValueKind: JsonValueKind.Object } element) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("code" or "errorCode" or "reason")) continue;
            var candidate = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (candidate is null || candidate.Length is < 1 or > 64
                || candidate.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '_' and not '-' and not '.')) continue;
            providerCode = candidate;
            return true;
        }
        return false;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}

internal sealed record CopilotValidationFailure(ProviderCredentialValidationKind Kind,
    string? ErrorCode, int? UpstreamStatus, string ExceptionType);

internal sealed record GitHubExternalIdentity(string Id, string Login);

internal sealed record CopilotAccessProbe(
    int StatusCode,
    string ResultCode,
    string SafeMessage,
    ProviderCredentialValidationKind FailureKind,
    string Operation,
    string EndpointHost,
    string EndpointPath,
    string RequestId,
    string TokenCategory,
    string OAuthScopes,
    string AcceptedOAuthScopes,
    string ClientIdFingerprint);

internal sealed class GitHubIdentityValidationException(
    ProviderCredentialValidationKind kind,
    int? statusCode,
    string errorCode) : Exception("GitHub identity validation failed.")
{
    public ProviderCredentialValidationKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;
}

internal interface ICopilotRuntime
{
    Task<CopilotRuntimeInspection> InspectAsync(string accessToken, CancellationToken token);
    Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string accessToken, CancellationToken token);
    Task<CopilotRuntimeResult> ExecuteAsync(string accessToken, string model, string systemPrompt,
        string input, IReadOnlyCollection<ToolDefinition> tools,
        IReadOnlyCollection<ToolInvocationResult> priorResults, CancellationToken token);
}

public sealed record CopilotRuntimeResult(string? Content, IReadOnlyCollection<ToolCallRequest> ToolCalls);
internal sealed record CopilotRuntimeInspection(IReadOnlyCollection<ProviderModelDto> Models,
    ProviderMetricSource QuotaSource, string QuotaState, Exception? QuotaFailure = null,
    bool IsAuthenticated = false, string AuthType = "Unavailable", string CopilotPlan = "Unavailable",
    bool? ChatEnabled = null);

internal sealed class CopilotRuntimeOperationException(string operation, Exception innerException)
    : Exception($"Copilot runtime operation {operation} failed.", innerException)
{
    internal string Operation { get; } = operation;
}

internal static class CopilotQuotaInspector
{
    internal static string Classify(JsonElement payload)
    {
        if (!TryGetProperty(payload, "quotaSnapshots", out var snapshots)
            || snapshots.ValueKind != JsonValueKind.Object)
            return "Unavailable";

        var found = false;
        var hasAvailable = false;
        var allKnownExhausted = true;
        foreach (var property in snapshots.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            found = true;
            var snapshot = property.Value;
            var unlimited = TryReadBoolean(snapshot, "isUnlimitedEntitlement", out var isUnlimited) && isUnlimited;
            var overageAllowed = TryReadBoolean(snapshot, "overageAllowedWithExhaustedQuota", out var overage)
                && overage;
            var usageAllowed = TryReadBoolean(snapshot, "usageAllowedWithExhaustedQuota", out var usage) && usage;
            var hasRemaining = TryReadNumber(snapshot, "remainingPercentage", out var remaining)
                && remaining > 0;
            if (unlimited || overageAllowed || usageAllowed || hasRemaining)
            {
                hasAvailable = true;
                allKnownExhausted = false;
                continue;
            }

            var explicitlyExhausted = TryReadNumber(snapshot, "remainingPercentage", out remaining)
                && remaining <= 0;
            if (!explicitlyExhausted) allKnownExhausted = false;
        }

        if (!found) return "Unavailable";
        if (hasAvailable) return "Available";
        return allKnownExhausted ? "Exhausted" : "Unavailable";
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    result = property.Value;
                    return true;
                }
        result = default;
        return false;
    }

    private static bool TryReadBoolean(JsonElement value, string name, out bool result)
    {
        result = false;
        if (!TryGetProperty(value, name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = property.GetBoolean();
        return true;
    }

    private static bool TryReadNumber(JsonElement value, string name, out decimal result)
    {
        result = 0;
        return TryGetProperty(value, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out result);
    }
}

internal sealed class OfficialCopilotRuntime : ICopilotRuntime
{
    public async Task<CopilotRuntimeInspection> InspectAsync(string accessToken, CancellationToken token)
    {
        await using var client = Create(accessToken);
        await StartAsync(client, token);
        var authStatus = await ReadAuthStatusAsync(client, token);
        var models = await ReadModelsAsync(client, accessToken, token);
        try
        {
            dynamic quota = await client.Rpc.Account.GetQuotaAsync(null, accessToken, token);
            var state = CopilotQuotaInspector.Classify(JsonSerializer.SerializeToElement((object)quota));
            return new(models, state == "Unavailable" ? ProviderMetricSource.Unavailable
                : ProviderMetricSource.ProviderReported, state, IsAuthenticated: authStatus.IsAuthenticated,
                AuthType: authStatus.AuthType, CopilotPlan: authStatus.CopilotPlan,
                ChatEnabled: authStatus.ChatEnabled);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(models, ProviderMetricSource.Unavailable, "Unavailable", exception,
                authStatus.IsAuthenticated, authStatus.AuthType, authStatus.CopilotPlan,
                authStatus.ChatEnabled);
        }
    }

    private static async Task<CopilotRuntimeAuthStatus> ReadAuthStatusAsync(
        CopilotClient client, CancellationToken token)
    {
        var status = await client.GetAuthStatusAsync(token);
        return new(status.IsAuthenticated, status.AuthType ?? "Unavailable",
            "Unavailable", null);
    }

    public async Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string accessToken, CancellationToken token)
    {
        await using var client = Create(accessToken);
        await StartAsync(client, token);
        return await ReadModelsAsync(client, accessToken, token);
    }

    private static async Task<IReadOnlyCollection<ProviderModelDto>> ReadModelsAsync(
        CopilotClient client, string accessToken, CancellationToken token)
    {
        dynamic response;
        try
        {
            response = await client.Rpc.Models.ListAsync(null, accessToken, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw new CopilotRuntimeOperationException("models.list", exception);
        }

        var result = new List<ProviderModelDto>();
        foreach (dynamic model in response.Models)
        {
            string id = model.Id;
            string name = model.Name ?? id;
            result.Add(new(id, name, null, true, false, null, null));
        }
        if (result.Count == 0)
        {
            var fallback = await client.ListModelsAsync(token);
            result.AddRange(fallback.Select(model => new ProviderModelDto(
                model.Id, model.Name ?? model.Id, null, true, false, null, null)));
        }
        return result;
    }

    public async Task<CopilotRuntimeResult> ExecuteAsync(string accessToken, string model, string systemPrompt,
        string input, IReadOnlyCollection<ToolDefinition> tools,
        IReadOnlyCollection<ToolInvocationResult> priorResults, CancellationToken token)
    {
        await using var client = Create(accessToken);
        await StartAsync(client, token);
        var calls = new List<ToolCallRequest>();
        var copilotTools = tools.Select(definition => CreateTool(definition, calls)).ToArray();
        dynamic session = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = model,
                Streaming = false,
                Tools = copilotTools,
                GitHubToken = accessToken,
            }, token);
        var priorContext = priorResults.Count == 0 ? string.Empty
            : "\n\nPreviously completed server tool results (do not repeat their call IDs):\n"
                + JsonSerializer.Serialize(priorResults.Select(result => new
                {
                    result.CallId,
                    result.ToolName,
                    result.Succeeded,
                    result.Output,
                    result.Error,
                }));
        dynamic response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = $"{systemPrompt}\n\n{input}{priorContext}" }, token);
        return new(response?.Data?.Content, calls);
    }

    private static AIFunction CreateTool(ToolDefinition definition, List<ToolCallRequest> calls)
    {
        var toolOptions = new CopilotToolOptions { SkipPermission = true, Defer = CopilotToolDefer.Never };
        var factoryOptions = new AIFunctionFactoryOptions
        {
            Name = definition.Name,
            Description = definition.Description,
        };
        object Deferred(ToolInvocation invocation)
        {
            calls.Add(new(invocation.ToolCallId, definition.Name,
                JsonSerializer.Serialize(invocation.Arguments)));
            return new { deferred = true, callId = invocation.ToolCallId };
        }
        return definition.Name switch
        {
            "text.length" => CopilotTool.DefineTool(
                (string text, ToolInvocation invocation) => Deferred(invocation),
                toolOptions, factoryOptions),
            _ => CopilotTool.DefineTool(
                (ToolInvocation invocation) => Deferred(invocation),
                toolOptions, factoryOptions),
        };
    }

    private static CopilotClient Create(string accessToken) => new(CopilotClientConfiguration.CreateOptions(accessToken));

    private static async Task StartAsync(CopilotClient client, CancellationToken token)
    {
        try
        {
            await client.StartAsync(token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw new CopilotRuntimeOperationException("copilot.start", exception);
        }
    }
}

internal sealed record CopilotRuntimeAuthStatus(
    bool IsAuthenticated,
    string AuthType,
    string CopilotPlan,
    bool? ChatEnabled);

internal static class CopilotClientConfiguration
{
    internal static CopilotClientOptions CreateOptions(string accessToken) => new()
    {
        GitHubToken = accessToken,
        UseLoggedInUser = false,
    };
}

internal sealed record GitHubOAuthCredential(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt)
{
    private const string Prefix = "github-oauth-v1:";
    public static string Serialize(string accessToken, string? refreshToken,
        DateTimeOffset? expiresAt, DateTimeOffset? refreshTokenExpiresAt) =>
        Prefix + JsonSerializer.Serialize(new GitHubOAuthCredential(
            accessToken, refreshToken, expiresAt, refreshTokenExpiresAt));
    public static GitHubOAuthCredential Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("GitHub OAuth token is required.");
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return new(value, null, null, null);
        return JsonSerializer.Deserialize<GitHubOAuthCredential>(value[Prefix.Length..])
            ?? throw new InvalidOperationException("GitHub OAuth credential is invalid.");
    }
}
