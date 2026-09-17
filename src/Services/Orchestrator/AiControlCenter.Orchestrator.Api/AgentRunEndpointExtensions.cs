using System.IdentityModel.Tokens.Jwt;
using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using AiControlCenter.Security;

namespace AiControlCenter.Orchestrator.Api;

public static class AgentRunEndpointExtensions
{
    public static IEndpointRouteBuilder MapAgentRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var runs = endpoints.MapGroup("/v1/runs")
            .RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);
        runs.MapPost("/", async (
            CreateAgentRunRequest request,
            HttpContext context,
            AgentRunApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var ownerId = GetUserId(context.User);
            var authorization = context.Request.Headers.Authorization.ToString();
            var run = await service.CreateAsync(
                request.ToCommand(ownerId, context.TraceIdentifier, authorization), cancellationToken);
            return Results.Json(run, statusCode: StatusCodes.Status202Accepted);
        }).AddEndpointFilter<ValidationFilter<CreateAgentRunRequest>>();
        runs.MapGet("/", async (
            AgentRunStatus? status, Guid? agentId, int? page, int? pageSize,
            HttpContext context, AgentRunApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(new ListAgentRunsQuery(
                GetUserId(context.User), context.User.IsInRole("Admin"), status, agentId,
                page ?? 1, pageSize ?? 20), cancellationToken)));
        runs.MapGet("/{id:guid}", async (
            Guid id, HttpContext context, AgentRunApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(new GetAgentRunQuery(
                id, GetUserId(context.User), context.User.IsInRole("Admin")), cancellationToken)));
        return endpoints;
    }

    private static Guid GetUserId(System.Security.Claims.ClaimsPrincipal user)
    {
        var value = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("Authenticated token does not contain a valid subject.");
    }
}
