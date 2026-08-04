namespace AiControlCenter.Identity.Api;

public sealed class OriginValidationFilter(IConfiguration configuration) : IEndpointFilter
{
    private readonly HashSet<string> allowedOrigins = configuration
        .GetSection("Security:AllowedOrigins")
        .Get<string[]>()?
        .Select(origin => origin.TrimEnd('/'))
        .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? [];

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var origin = context.HttpContext.Request.Headers.Origin.ToString().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin) || !allowedOrigins.Contains(origin))
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Request origin is not allowed."));
        }

        return next(context);
    }
}
