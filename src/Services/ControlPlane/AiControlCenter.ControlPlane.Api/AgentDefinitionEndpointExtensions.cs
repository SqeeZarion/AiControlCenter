using System.IdentityModel.Tokens.Jwt;
using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using AiControlCenter.Security;

namespace AiControlCenter.ControlPlane.Api;

public static class AgentDefinitionEndpointExtensions
{
    public static IEndpointRouteBuilder MapAgentDefinitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var agents = endpoints.MapGroup("/v1/agents")
            .RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

        agents.MapGet("/", async (
            bool? includeArchived, AgentDefinitionStatus? status, Guid? directionId,
            string? search, int? page, int? pageSize,
            AgentDefinitionApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(new ListAgentDefinitionsQuery(
                includeArchived ?? false, status, directionId, search, page ?? 1, pageSize ?? 20),
                cancellationToken)));
        agents.MapGet("/{id:guid}", async (
            Guid id, AgentDefinitionApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(id, cancellationToken)));

        var mutations = agents.MapGroup(string.Empty)
            .RequireAuthorization(SecurityPolicyNames.AdminOrDeveloper);
        mutations.MapPost("/", async (
            HttpContext context,
            CreateAgentDefinitionRequest request,
            AgentDefinitionApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var agent = await service.CreateAsync(UserId(context), request.ToCommand(), cancellationToken);
            return Results.Json(agent, statusCode: StatusCodes.Status201Created);
        }).AddEndpointFilter<ValidationFilter<CreateAgentDefinitionRequest>>();
        mutations.MapPut("/{id:guid}", async (
            HttpContext context, Guid id, UpdateAgentDefinitionRequest request,
            AgentDefinitionApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateAsync(id, UserId(context), request.ToCommand(), cancellationToken)))
            .AddEndpointFilter<ValidationFilter<UpdateAgentDefinitionRequest>>();
        mutations.MapPatch("/{id:guid}/status", async (
            Guid id, ChangeAgentDefinitionStatusCommand command,
            AgentDefinitionApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ChangeStatusAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ChangeAgentDefinitionStatusCommand>>();
        mutations.MapPost("/{id:guid}/archive", async (
            Guid id, ArchiveAgentDefinitionCommand command,
            AgentDefinitionApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ArchiveAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ArchiveAgentDefinitionCommand>>();
        mutations.MapPost("/{id:guid}/restore", async (
            Guid id, RestoreAgentDefinitionCommand command,
            AgentDefinitionApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RestoreAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<RestoreAgentDefinitionCommand>>();

        return endpoints;
    }

    private static Guid UserId(HttpContext context)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(subject, out var owner) && owner != Guid.Empty
            ? owner
            : throw new BadHttpRequestException("Authenticated user id is invalid.");
    }
}
