using System.Text.Json;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Infrastructure.Providers;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiControlCenter.Integrations.Api;

public sealed partial class IntegrationsExceptionHandler(ILogger<IntegrationsExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Provider connection not found."),
            LocalCopilotCliAccessDeniedException =>
                (StatusCodes.Status404NotFound, "Local Copilot CLI is not available."),
            ProviderCredentialRejectedException =>
                (StatusCodes.Status422UnprocessableEntity, exception.Message),
            ArgumentException or BadHttpRequestException or JsonException =>
                (StatusCodes.Status400BadRequest, "Validation failed."),
            GitHubOAuthNotConfiguredException =>
                (StatusCodes.Status409Conflict, "GitHub OAuth не налаштовано"),
            ProviderFeatureDisabledException =>
                (StatusCodes.Status409Conflict, exception.Message),
            LocalCopilotManagementException localManagementError =>
                (localManagementError.StatusCode, SafeLocalCopilotTitle(localManagementError.Code)),
            InvalidOperationException => (StatusCodes.Status409Conflict, exception.Message),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        LogRejectedRequest(logger, status, httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.com/{status}",
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        };
        if (exception is GitHubOAuthNotConfiguredException)
            details.Extensions["code"] = nameof(GitHubOAuthConfigurationStatus.NotConfigured);
        if (exception is ProviderCredentialRejectedException credentialError)
            details.Extensions["code"] = credentialError.Code;
        if (exception is ProviderFeatureDisabledException featureError)
            details.Extensions["code"] = featureError.Code;
        if (exception is LocalCopilotCliAccessDeniedException)
            details.Extensions["code"] = "LocalCopilotCliNotAvailable";
        if (exception is LocalCopilotManagementException managementError)
            details.Extensions["code"] = managementError.Code;
        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body, details, cancellationToken: cancellationToken);
        return true;
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "Integrations request rejected with status {StatusCode} for {Method} {Path}")]
    private static partial void LogRejectedRequest(
        ILogger logger,
        int statusCode,
        string method,
        PathString path);

    private static string SafeLocalCopilotTitle(string code) => code switch
    {
        "SidecarUnavailable" => "Local Copilot sidecar недоступний.",
        "RuntimeUnavailable" => "Local Copilot runtime недоступний.",
        "Unauthenticated" => "Local Copilot CLI потребує авторизації.",
        "InsufficientPermissions" => "Недостатньо прав для Local Copilot CLI.",
        "ValidationTimeout" => "Перевірка Local Copilot CLI перевищила час очікування.",
        "ModelCatalogUnavailable" => "Каталог моделей Local Copilot CLI недоступний.",
        "ConfigurationError" => "Конфігурація Local Copilot CLI некоректна.",
        "RateLimited" => "Забагато запитів до Local Copilot CLI. Повторіть пізніше.",
        "LocalCopilotReauthenticationUnsupported" =>
            "Bundled Copilot CLI не підтримує безпечну повторну авторизацію.",
        "LocalCopilotLogoutUnsupported" =>
            "Bundled Copilot CLI не підтримує стабільний logout.",
        _ => "Local Copilot operation failed.",
    };
}
