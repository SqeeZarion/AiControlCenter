using System.Text.Json;
using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiControlCenter.ControlPlane.Api;

public sealed partial class ControlPlaneExceptionHandler(ILogger<ControlPlaneExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            DirectionNotFoundException => (StatusCodes.Status404NotFound, "Direction not found."),
            AgentDefinitionNotFoundException or AgentDirectionNotFoundException =>
                (StatusCodes.Status404NotFound, "Agent definition or direction not found."),
            DirectionConflictException or DirectionRuleViolationException =>
                (StatusCodes.Status409Conflict, exception.Message),
            AgentDefinitionConflictException or AgentDefinitionRuleViolationException =>
                (StatusCodes.Status409Conflict, exception.Message),
            AgentModelRouteValidationException routeFailure =>
                (routeFailure.StatusCode, exception.Message),
            AgentNotRunnableException => (StatusCodes.Status409Conflict, exception.Message),
            ValidationException or ArgumentException or BadHttpRequestException or JsonException =>
                (StatusCodes.Status400BadRequest, "Validation failed."),
            _ => (0, string.Empty),
        };
        if (status == 0)
        {
            return false;
        }

        LogRejectedRequest(logger, status, httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.com/{status}",
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = httpContext.TraceIdentifier,
                ["correlationId"] = httpContext.TraceIdentifier },
        };
        if (exception is AgentModelRouteValidationException routeValidation)
        {
            details.Title = routeValidation.Message;
            details.Extensions["code"] = routeValidation.Code;
            details.Extensions["field"] = routeValidation.Field;
        }
        else if (exception is ValidationException validation)
        {
            var first = validation.Errors.FirstOrDefault();
            details.Extensions["code"] = "AgentDefinitionValidationFailed";
            details.Extensions["field"] = first?.PropertyName ?? string.Empty;
            details.Extensions["errors"] = validation.Errors
                .GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key,
                    group => group.Select(error => error.ErrorMessage).Distinct().ToArray());
        }
        await httpContext.Response.WriteAsJsonAsync(details, cancellationToken);
        return true;
    }

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "ControlPlane request rejected with status {StatusCode} for {Method} {Path}")]
    private static partial void LogRejectedRequest(
        ILogger logger,
        int statusCode,
        string method,
        PathString path);
}
