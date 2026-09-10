using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Application;

public sealed class DirectionApplicationService(
    IDirectionRepository directions,
    IControlPlaneUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IValidator<ListDirectionsQuery> listValidator,
    IValidator<GetDirectionQuery> getValidator,
    IValidator<CreateDirectionCommand> createValidator,
    IValidator<UpdateDirectionCommand> updateValidator,
    IValidator<ChangeDirectionStatusCommand> statusValidator,
    IValidator<ChangeDirectionSortOrderCommand> sortOrderValidator,
    IValidator<ArchiveDirectionCommand> archiveValidator,
    IValidator<RestoreDirectionCommand> restoreValidator)
{
    public async Task<DirectionListDto> ListAsync(
        ListDirectionsQuery query,
        CancellationToken cancellationToken)
    {
        await listValidator.ValidateAndThrowAsync(query, cancellationToken);
        var page = await directions.ListAsync(query with { Search = query.Search?.Trim() }, cancellationToken);
        return new DirectionListDto(
            page.Items.Select(ToDto).ToArray(),
            query.Page,
            query.PageSize,
            page.TotalCount);
    }

    public async Task<DirectionDto> GetAsync(GetDirectionQuery query, CancellationToken cancellationToken)
    {
        await getValidator.ValidateAndThrowAsync(query, cancellationToken);
        return ToDto(await RequireAsync(query.Id, tracking: false, cancellationToken));
    }

    public async Task<DirectionDto> CreateAsync(
        CreateDirectionCommand command,
        CancellationToken cancellationToken)
    {
        await createValidator.ValidateAndThrowAsync(command, cancellationToken);
        var code = Direction.NormalizeCode(command.Code);
        await EnsureCodeAvailableAsync(code, excludingId: null, cancellationToken);
        var direction = Direction.Create(
            Guid.NewGuid(),
            command.Name,
            code,
            command.Description,
            command.Icon,
            command.Status,
            command.SortOrder,
            timeProvider.GetUtcNow());
        directions.Add(direction);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(direction);
    }

    public async Task<DirectionDto> UpdateAsync(
        Guid id,
        UpdateDirectionCommand command,
        CancellationToken cancellationToken)
    {
        await updateValidator.ValidateAndThrowAsync(command, cancellationToken);
        var direction = await RequireCurrentAsync(id, command.Version, cancellationToken);
        var code = Direction.NormalizeCode(command.Code);
        await EnsureCodeAvailableAsync(code, id, cancellationToken);
        direction.Update(
            command.Name,
            code,
            command.Description,
            command.Icon,
            command.Status,
            command.SortOrder,
            timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(direction);
    }

    public async Task<DirectionDto> ChangeStatusAsync(
        Guid id,
        ChangeDirectionStatusCommand command,
        CancellationToken cancellationToken)
    {
        await statusValidator.ValidateAndThrowAsync(command, cancellationToken);
        var direction = await RequireCurrentAsync(id, command.Version, cancellationToken);
        direction.ChangeStatus(command.Status, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(direction);
    }

    public async Task<DirectionDto> ChangeSortOrderAsync(
        Guid id,
        ChangeDirectionSortOrderCommand command,
        CancellationToken cancellationToken)
    {
        await sortOrderValidator.ValidateAndThrowAsync(command, cancellationToken);
        var direction = await RequireCurrentAsync(id, command.Version, cancellationToken);
        direction.ChangeSortOrder(command.SortOrder, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(direction);
    }

    public async Task<DirectionDto> ArchiveAsync(
        Guid id,
        ArchiveDirectionCommand command,
        CancellationToken cancellationToken)
    {
        await archiveValidator.ValidateAndThrowAsync(command, cancellationToken);
        var direction = await RequireCurrentAsync(id, command.Version, cancellationToken);
        if (direction.Archive(timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ToDto(direction);
    }

    public async Task<DirectionDto> RestoreAsync(
        Guid id,
        RestoreDirectionCommand command,
        CancellationToken cancellationToken)
    {
        await restoreValidator.ValidateAndThrowAsync(command, cancellationToken);
        var direction = await RequireCurrentAsync(id, command.Version, cancellationToken);
        if (direction.Restore(timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ToDto(direction);
    }

    private async Task<Direction> RequireCurrentAsync(
        Guid id,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        var direction = await RequireAsync(id, tracking: true, cancellationToken);
        if (direction.Version != expectedVersion)
        {
            throw new DirectionConflictException("Direction was changed by another request. Reload and retry.");
        }

        return direction;
    }

    private async Task<Direction> RequireAsync(Guid id, bool tracking, CancellationToken cancellationToken) =>
        await directions.GetByIdAsync(id, tracking, cancellationToken)
        ?? throw new DirectionNotFoundException(id);

    private async Task EnsureCodeAvailableAsync(
        string code,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        if (await directions.CodeExistsAsync(code, excludingId, cancellationToken))
        {
            throw new DirectionConflictException("A direction with this code already exists.");
        }
    }

    private static DirectionDto ToDto(Direction direction) => new(
        direction.Id,
        direction.Name,
        direction.Code,
        direction.Description,
        direction.Icon,
        direction.Status,
        direction.SortOrder,
        direction.CreatedAt,
        direction.UpdatedAt,
        direction.ArchivedAt,
        direction.Version);
}
