using AiControlCenter.Security;

namespace AiControlCenter.Gateway.Operations;

public static class OperationsCenterEndpointExtensions
{
    public static IEndpointRouteBuilder MapOperationsCenterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/gateway/v1/operations/snapshot",
            async (HttpContext context, IOperationsCenterService service, CancellationToken cancellationToken) =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                return Results.Ok(await service.GetSnapshotAsync(authorization, cancellationToken));
            })
            .WithName("GetOperationsCenterSnapshot")
            .RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

        return endpoints;
    }
}
