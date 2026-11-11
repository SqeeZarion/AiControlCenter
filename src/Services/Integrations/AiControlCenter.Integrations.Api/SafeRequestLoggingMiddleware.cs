using System.Diagnostics;

namespace AiControlCenter.Integrations.Api;

public sealed partial class SafeRequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<SafeRequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LogRequest(
                    logger,
                    context.Request.Method,
                    context.Request.Path.Value ?? string.Empty,
                    context.Response.StatusCode,
                    elapsedMilliseconds,
                    context.TraceIdentifier);
            }
        }
    }

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Information,
        Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds:F1} ms; correlation {CorrelationId}")]
    private static partial void LogRequest(
        ILogger logger,
        string method,
        string path,
        int statusCode,
        double elapsedMilliseconds,
        string correlationId);
}
