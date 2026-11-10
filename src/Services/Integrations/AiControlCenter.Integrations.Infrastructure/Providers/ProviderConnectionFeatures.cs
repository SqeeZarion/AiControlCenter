using System.Net;
using System.Net.Http.Json;
using AiControlCenter.Integrations.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

public sealed class ProviderConnectionFeatureOptions
{
    public const string SectionName = "ProviderConnectionFeatures";
    public bool GitHubCopilotOAuthEnabled { get; init; }
    public bool GitHubCopilotPatEnabled { get; init; }
    public bool LocalCopilotCliEnabled { get; init; }
    public bool LocalCopilotCliReauthenticationEnabled { get; init; }
    public bool LocalCopilotCliLogoutEnabled { get; init; }
}

public sealed record ProviderConnectionCapabilitiesDto(
    bool GitHubCopilotOAuthEnabled,
    bool GitHubCopilotPatEnabled,
    bool LocalCopilotCliEnabled,
    bool LocalCopilotCliReauthenticationEnabled,
    bool LocalCopilotCliLogoutEnabled);

public sealed class ProviderFeatureDisabledException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ProviderConnectionFeatureAccess(IOptions<ProviderConnectionFeatureOptions> options)
{
    private readonly ProviderConnectionFeatureOptions value = options.Value;

    public ProviderConnectionCapabilitiesDto Capabilities(
        bool localCopilotAvailableForCurrentUser = true)
    {
        var localCopilotEnabled = value.LocalCopilotCliEnabled
            && localCopilotAvailableForCurrentUser;
        return new(
        value.GitHubCopilotOAuthEnabled,
        value.GitHubCopilotPatEnabled,
        localCopilotEnabled,
        localCopilotEnabled && value.LocalCopilotCliReauthenticationEnabled,
        localCopilotEnabled && value.LocalCopilotCliLogoutEnabled);
    }

    public void EnsureOAuthEnabled() => Ensure(value.GitHubCopilotOAuthEnabled,
        "GitHubCopilotOAuthDisabled", "GitHub Copilot OAuth вимкнено конфігурацією сервера.");
    public void EnsurePatEnabled() => Ensure(value.GitHubCopilotPatEnabled,
        "GitHubCopilotPatDisabled", "GitHub Copilot PAT вимкнено конфігурацією сервера.");
    public void EnsureLocalCliEnabled() => Ensure(value.LocalCopilotCliEnabled,
        "LocalCopilotCliDisabled", "Local Copilot CLI вимкнено конфігурацією сервера.");
    public void EnsureLocalCliReauthenticationEnabled() => Ensure(
        value.LocalCopilotCliReauthenticationEnabled,
        "LocalCopilotReauthenticationUnsupported",
        "Bundled Copilot CLI не підтримує безпечну повторну авторизацію зі збереженням чинної сесії.");
    public void EnsureLocalCliLogoutEnabled() => Ensure(value.LocalCopilotCliLogoutEnabled,
        "LocalCopilotLogoutUnsupported",
        "Bundled Copilot CLI не підтримує стабільний non-interactive logout.");

    private static void Ensure(bool enabled, string code, string message)
    { if (!enabled) throw new ProviderFeatureDisabledException(code, message); }
}

public enum LocalCopilotReconnectState
{
    Starting,
    StartingLogin,
    WaitingForUser,
    GitHubAuthorized,
    PersistingCredential,
    RestartingRuntime,
    Validating,
    SynchronizingModels,
    Cancelling,
    Authorized,
    LoginFailed,
    AuthorizationFailed,
    RuntimeRestartFailed,
    ValidationFailed,
    ModelSyncFailed,
    Expired,
    Cancelled,
}

public sealed record LocalCopilotReconnectDto(
    Guid OperationId,
    LocalCopilotReconnectState State,
    string? VerificationUri,
    string? UserCode,
    DateTimeOffset ExpiresAt,
    string? ErrorCode,
    Guid OwnerId = default,
    string? CorrelationId = null,
    int? ProcessExitCode = null,
    string? FailureReason = null,
    bool CanCancel = false);
public sealed record LocalCopilotLogoutDto(string Status, string? ErrorCode);

public sealed class LocalCopilotManagementException(
    string code,
    int statusCode,
    string safeMessage,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public interface ILocalCopilotReconnectStateClient
{
    Task<LocalCopilotReconnectDto> GetAsync(Guid operationId, CancellationToken token);
    Task<LocalCopilotReconnectDto> TransitionAsync(
        Guid operationId,
        LocalCopilotReconnectState state,
        string? errorCode,
        CancellationToken token);
}

public sealed class LocalCopilotReconnectClient(
    HttpClient httpClient,
    IOptions<LocalCopilotCliOptions> options) : ILocalCopilotReconnectStateClient
{
    private readonly LocalCopilotCliOptions value = options.Value;

    public Task<LocalCopilotReconnectDto> StartAsync(
        Guid ownerId,
        string correlationId,
        CancellationToken token) => SendAsync(HttpMethod.Post, "/v1/reconnect", token,
            JsonContent.Create(new { ownerId, correlationId }));

    public Task<LocalCopilotReconnectDto> StartForceReauthenticateAsync(
        bool confirmed,
        Guid ownerId,
        string correlationId,
        CancellationToken token) => SendAsync(HttpMethod.Post, "/v1/reauthenticate", token,
            JsonContent.Create(new { confirmed, ownerId, correlationId }));

    public Task<LocalCopilotReconnectDto> GetAsync(Guid operationId, CancellationToken token) =>
        SendAsync(HttpMethod.Get, $"/v1/reconnect/{operationId:D}", token);

    public async Task<LocalCopilotReconnectDto?> GetCurrentAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/reconnect/current");
        request.Headers.Add("X-AiControlCenter-Local-Copilot-Token", value.ConnectionToken);
        using var response = await SendHttpAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, token);
        var current = await response.Content.ReadFromJsonAsync<SidecarReconnectDto>(token);
        return current is null ? null : Map(current);
    }

    public Task<LocalCopilotReconnectDto> CancelAsync(Guid operationId, CancellationToken token) =>
        SendAsync(HttpMethod.Post, $"/v1/reconnect/{operationId:D}/cancel", token);

    public async Task<LocalCopilotLogoutDto> LogoutAsync(bool confirmed, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logout")
        { Content = JsonContent.Create(new { confirmed }) };
        request.Headers.Add("X-AiControlCenter-Local-Copilot-Token", value.ConnectionToken);
        using var response = await SendHttpAsync(request, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<LocalCopilotLogoutDto>(token)
            ?? throw new HttpRequestException("Local Copilot sidecar returned an empty logout response.");
    }

    public Task<LocalCopilotReconnectDto> TransitionAsync(
        Guid operationId,
        LocalCopilotReconnectState state,
        string? errorCode,
        CancellationToken token) => SendAsync(HttpMethod.Post,
            $"/v1/reconnect/{operationId:D}/state", token,
            JsonContent.Create(new { state = state.ToString(), errorCode }));

    private async Task<LocalCopilotReconnectDto> SendAsync(
        HttpMethod method,
        string path,
        CancellationToken token,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Content = content;
        request.Headers.Add("X-AiControlCenter-Local-Copilot-Token", value.ConnectionToken);
        using var response = await SendHttpAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var existing = await response.Content.ReadFromJsonAsync<SidecarReconnectDto>(token);
            if (existing is not null) return Map(existing);
        }
        await EnsureSuccessAsync(response, token);
        return Map(await response.Content.ReadFromJsonAsync<SidecarReconnectDto>(token)
            ?? throw new HttpRequestException("Local Copilot sidecar returned an empty response."));
    }

    private static LocalCopilotReconnectDto Map(SidecarReconnectDto value) => new(
        value.Id, Enum.Parse<LocalCopilotReconnectState>(value.State, true), value.VerificationUri,
        value.UserCode, value.ExpiresAt, value.ErrorCode, value.OwnerId, value.CorrelationId,
        value.ProcessExitCode, value.FailureReason, value.CanCancel);

    private sealed record SidecarReconnectDto(Guid Id, string State, string? VerificationUri,
        string? UserCode, DateTimeOffset ExpiresAt, string? ErrorCode,
        Guid OwnerId = default, string? CorrelationId = null, int? ProcessExitCode = null,
        string? FailureReason = null, bool CanCancel = false);

    private async Task<HttpResponseMessage> SendHttpAsync(
        HttpRequestMessage request,
        CancellationToken token)
    {
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new LocalCopilotManagementException(
                "ValidationTimeout", 504,
                "Local Copilot sidecar response timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new LocalCopilotManagementException(
                "SidecarUnavailable", 503,
                "Local Copilot sidecar is unavailable.", exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var sidecar = await TryReadErrorAsync(response, token);
        var code = sidecar?.Code ?? sidecar?.ErrorCode ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "ConfigurationError",
            HttpStatusCode.Forbidden => "InsufficientPermissions",
            HttpStatusCode.NotFound => "SidecarOperationNotFound",
            HttpStatusCode.Conflict => "OperationConflict",
            HttpStatusCode.TooManyRequests => "RateLimited",
            _ when (int)response.StatusCode >= 500 => "SidecarUnavailable",
            _ => "LocalCopilotRequestRejected",
        };
        throw new LocalCopilotManagementException(
            code, (int)response.StatusCode, "Local Copilot management request was rejected.");
    }

    private static async Task<SidecarErrorDto?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<SidecarErrorDto>(token);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record SidecarErrorDto(string? Code, string? ErrorCode);
}

public interface ILocalCopilotConnectionCompletion
{
    Task<ProviderCredentialValidationResult> ValidateAsync(CancellationToken token);
    Task SynchronizeAsync(
        Guid owner,
        ProviderCredentialValidationResult validation,
        CancellationToken token);
}

internal sealed class LocalCopilotConnectionCompletion(ProviderConnectionService service)
    : ILocalCopilotConnectionCompletion
{
    public Task<ProviderCredentialValidationResult> ValidateAsync(CancellationToken token) =>
        service.ValidateLocalCopilotCliAsync(token);

    public async Task SynchronizeAsync(
        Guid owner,
        ProviderCredentialValidationResult validation,
        CancellationToken token) =>
        _ = await service.SynchronizeLocalCopilotCliAsync(owner, validation, token);
}

public sealed partial class LocalCopilotReconnectWorkflow(
    ILocalCopilotConnectionCompletion completion,
    ILocalCopilotReconnectStateClient reconnect,
    ILogger<LocalCopilotReconnectWorkflow> logger)
{
    public async Task CompleteAsync(Guid owner, Guid operationId, CancellationToken token)
    {
        ProviderCredentialValidationResult validation;
        try
        {
            validation = await completion.ValidateAsync(token);
        }
        catch (ProviderCredentialRejectedException exception)
        {
            var state = exception.Code == ProviderCredentialValidationKind.ModelCatalogUnavailable.ToString()
                ? LocalCopilotReconnectState.ModelSyncFailed
                : LocalCopilotReconnectState.ValidationFailed;
            await reconnect.TransitionAsync(operationId, state, SafeCode(exception.Code), token);
            LogCompletionFailure(logger, operationId, state.ToString(), exception.GetType().Name);
            return;
        }
        catch (Exception exception)
        {
            await reconnect.TransitionAsync(operationId, LocalCopilotReconnectState.ValidationFailed,
                "LocalCopilotValidationFailed", CancellationToken.None);
            LogCompletionFailure(logger, operationId, "Validating", exception.GetType().Name);
            return;
        }

        await reconnect.TransitionAsync(operationId,
            LocalCopilotReconnectState.SynchronizingModels, null, token);
        try
        {
            await completion.SynchronizeAsync(owner, validation, token);
            await reconnect.TransitionAsync(operationId,
                LocalCopilotReconnectState.Authorized, null, token);
        }
        catch (Exception exception)
        {
            await reconnect.TransitionAsync(operationId,
                LocalCopilotReconnectState.ModelSyncFailed,
                "LocalCopilotModelSyncFailed", CancellationToken.None);
            LogCompletionFailure(logger, operationId, "SynchronizingModels", exception.GetType().Name);
        }
    }

    private static string SafeCode(string code) => code switch
    {
        nameof(ProviderCredentialValidationKind.InvalidCredential) => code,
        nameof(ProviderCredentialValidationKind.RequiresReconnect) => code,
        nameof(ProviderCredentialValidationKind.NotEntitled) => code,
        nameof(ProviderCredentialValidationKind.PolicyBlocked) => code,
        nameof(ProviderCredentialValidationKind.InsufficientPermissions) => code,
        nameof(ProviderCredentialValidationKind.RateLimited) => code,
        nameof(ProviderCredentialValidationKind.TemporarilyUnavailable) => code,
        nameof(ProviderCredentialValidationKind.ModelCatalogUnavailable) => code,
        "LocalCopilotIdentityUnavailable" => code,
        _ => "LocalCopilotValidationFailed",
    };

    [LoggerMessage(EventId = 6202, Level = LogLevel.Warning,
        Message = "Local Copilot reconnect {OperationId} stopped at {Stage} with {ExceptionType}.")]
    private static partial void LogCompletionFailure(
        ILogger logger, Guid operationId, string stage, string exceptionType);
}
