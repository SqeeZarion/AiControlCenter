using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using AiControlCenter.Security;

namespace AiControlCenter.ControlPlane.Api;

public static class DirectionEndpointExtensions
{
    public static IEndpointRouteBuilder MapDirectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var directions = endpoints.MapGroup("/v1/directions")
            .RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged);

        directions.MapGet("/", async (
            bool? includeArchived,
            DirectionStatus? status,
            string? search,
            int? page,
            int? pageSize,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new ListDirectionsQuery(
                    includeArchived ?? false,
                    status,
                    search,
                    page ?? 1,
                    pageSize ?? ListDirectionsQuery.DefaultPageSize),
                cancellationToken)));
        directions.MapGet("/{id:guid}", async (
            Guid id,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(new GetDirectionQuery(id), cancellationToken)));

        var mutations = directions.MapGroup(string.Empty)
            .RequireAuthorization(SecurityPolicyNames.AdminOnly);
        mutations.MapPost("/", async (
            CreateDirectionCommand command,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var direction = await service.CreateAsync(command, cancellationToken);
            return Results.Json(direction, statusCode: StatusCodes.Status201Created);
        }).AddEndpointFilter<ValidationFilter<CreateDirectionCommand>>();
        mutations.MapPut("/{id:guid}", async (
            Guid id,
            UpdateDirectionRequest request,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateAsync(id, request.ToCommand(), cancellationToken)))
            .AddEndpointFilter<ValidationFilter<UpdateDirectionRequest>>();
        mutations.MapPatch("/{id:guid}/status", async (
            Guid id,
            ChangeDirectionStatusCommand command,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ChangeStatusAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ChangeDirectionStatusCommand>>();
        mutations.MapPatch("/{id:guid}/sort-order", async (
            Guid id,
            ChangeDirectionSortOrderCommand command,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ChangeSortOrderAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ChangeDirectionSortOrderCommand>>();
        mutations.MapPost("/{id:guid}/archive", async (
            Guid id,
            ArchiveDirectionCommand command,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ArchiveAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ArchiveDirectionCommand>>();
        mutations.MapPost("/{id:guid}/restore", async (
            Guid id,
            RestoreDirectionCommand command,
            DirectionApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.RestoreAsync(id, command, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<RestoreDirectionCommand>>();

        return endpoints;
    }
}
