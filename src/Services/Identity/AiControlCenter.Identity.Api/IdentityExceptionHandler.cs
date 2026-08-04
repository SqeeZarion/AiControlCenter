using AiControlCenter.Identity.Application;
using FluentValidation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiControlCenter.Identity.Api;

public sealed partial class IdentityExceptionHandler(ILogger<IdentityExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            IdentityAuthenticationException => (StatusCodes.Status401Unauthorized, "Authentication failed."),
            IdentityForbiddenException => (StatusCodes.Status403Forbidden, "Access is forbidden."),
            IdentityNotFoundException => (StatusCodes.Status404NotFound, "Resource not found."),
            IdentityConflictException => (StatusCodes.Status409Conflict, exception.Message),
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed."),
            AntiforgeryValidationException => (StatusCodes.Status400BadRequest, "Invalid antiforgery token."),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        LogSecurityFailure(logger, status, httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.com/{status}",
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        }, cancellationToken);
        return true;
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning, Message =
        "Identity request rejected with status {StatusCode} for {Method} {Path}")]
    private static partial void LogSecurityFailure(
        ILogger logger,
        int statusCode,
        string method,
        PathString path);
}
