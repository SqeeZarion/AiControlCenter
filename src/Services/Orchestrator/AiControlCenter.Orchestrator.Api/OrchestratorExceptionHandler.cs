using System.Text.Json;
using AiControlCenter.Orchestrator.Application;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiControlCenter.Orchestrator.Api;

public sealed partial class OrchestratorExceptionHandler(ILogger<OrchestratorExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            AgentRunNotFoundException or AgentRunAccessDeniedException => (404, "Run not found."),
            AgentRunAgentNotFoundException => (404, "Agent not found."),
            AgentRunConflictException => (409, exception.Message),
            AgentCatalogUnavailableException => (503, "Agent catalog is temporarily unavailable."),
            ValidationException or ArgumentException or BadHttpRequestException or JsonException =>
                (400, "Validation failed."),
            _ => (0, string.Empty),
        };
        if (status == 0) return false;
        LogRejectedRequest(logger, status, httpContext.Request.Method, httpContext.Request.Path);
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

    [LoggerMessage(EventId = 4100, Level = LogLevel.Warning,
        Message = "Orchestrator request rejected with status {StatusCode} for {Method} {Path}")]
    private static partial void LogRejectedRequest(ILogger logger, int statusCode, string method, PathString path);
}
