using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Application;

public sealed class AgentDefinitionApplicationService(
    IAgentDefinitionRepository agents,
    IControlPlaneUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IValidator<ListAgentDefinitionsQuery> listValidator,
    IValidator<CreateAgentDefinitionCommand> createValidator,
    IValidator<UpdateAgentDefinitionCommand> updateValidator,
    IValidator<ChangeAgentDefinitionStatusCommand> statusValidator,
    IValidator<ArchiveAgentDefinitionCommand> archiveValidator,
    IValidator<RestoreAgentDefinitionCommand> restoreValidator)
{
    public async Task<AgentDefinitionListDto> ListAsync(
        ListAgentDefinitionsQuery query,
        CancellationToken cancellationToken)
    {
        await listValidator.ValidateAndThrowAsync(query, cancellationToken);
        var page = await agents.ListAsync(query with { Search = query.Search?.Trim() }, cancellationToken);
        return new AgentDefinitionListDto(
            page.Items.Select(ToDto).ToArray(),
            query.Page,
            query.PageSize,
            page.TotalCount);
    }

    public async Task<AgentDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await RequireAsync(id, false, cancellationToken));

    public async Task<AgentDefinitionDto> CreateAsync(
        CreateAgentDefinitionCommand command,
        CancellationToken cancellationToken)
    {
        await createValidator.ValidateAndThrowAsync(command, cancellationToken);
        await EnsureDirectionExistsAsync(command.DirectionId, cancellationToken);
        var code = AgentDefinition.NormalizeCode(command.Code);
        await EnsureCodeAvailableAsync(code, null, cancellationToken);
        var agent = AgentDefinition.Create(
            Guid.NewGuid(), command.DirectionId, command.Name, code, command.Description,
            command.Status, command.ExecutionType, timeProvider.GetUtcNow());
        agents.Add(agent);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(agent);
    }

    public async Task<AgentDefinitionDto> UpdateAsync(
        Guid id,
        UpdateAgentDefinitionCommand command,
        CancellationToken cancellationToken)
    {
        await updateValidator.ValidateAndThrowAsync(command, cancellationToken);
        var agent = await RequireCurrentAsync(id, command.Version, cancellationToken);
        await EnsureDirectionExistsAsync(command.DirectionId, cancellationToken);
        var code = AgentDefinition.NormalizeCode(command.Code);
        await EnsureCodeAvailableAsync(code, id, cancellationToken);
        agent.Update(command.DirectionId, command.Name, code, command.Description,
            command.Status, command.ExecutionType, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(agent);
    }

    public async Task<AgentDefinitionDto> ChangeStatusAsync(
        Guid id,
        ChangeAgentDefinitionStatusCommand command,
        CancellationToken cancellationToken)
    {
        await statusValidator.ValidateAndThrowAsync(command, cancellationToken);
        var agent = await RequireCurrentAsync(id, command.Version, cancellationToken);
        agent.ChangeStatus(command.Status, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(agent);
    }

    public async Task<AgentDefinitionDto> ArchiveAsync(
        Guid id,
        ArchiveAgentDefinitionCommand command,
        CancellationToken cancellationToken)
    {
        await archiveValidator.ValidateAndThrowAsync(command, cancellationToken);
        var agent = await RequireCurrentAsync(id, command.Version, cancellationToken);
        if (agent.Archive(timeProvider.GetUtcNow())) await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(agent);
    }

    public async Task<AgentDefinitionDto> RestoreAsync(
        Guid id,
        RestoreAgentDefinitionCommand command,
        CancellationToken cancellationToken)
    {
        await restoreValidator.ValidateAndThrowAsync(command, cancellationToken);
        var agent = await RequireCurrentAsync(id, command.Version, cancellationToken);
        if (agent.Restore(timeProvider.GetUtcNow())) await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(agent);
    }

    public async Task<RunnableAgentSnapshot> GetRunnableSnapshotAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var lookup = await agents.GetRunnableSnapshotAsync(id, cancellationToken);
        return lookup.Status switch
        {
            RunnableAgentLookupStatus.Runnable => lookup.Snapshot!,
            RunnableAgentLookupStatus.Missing => throw new AgentDefinitionNotFoundException(id),
            _ => throw new AgentNotRunnableException(
                "Agent and its direction must both be active and not archived before a run can start."),
        };
    }

    private async Task<AgentDefinition> RequireCurrentAsync(Guid id, uint version, CancellationToken cancellationToken)
    {
        var agent = await RequireAsync(id, true, cancellationToken);
        if (agent.Version != version)
        {
            throw new AgentDefinitionConflictException(
                "Agent definition was changed by another request. Reload and retry.");
        }

        return agent;
    }

    private async Task<AgentDefinition> RequireAsync(Guid id, bool tracking, CancellationToken cancellationToken) =>
        await agents.GetByIdAsync(id, tracking, cancellationToken)
        ?? throw new AgentDefinitionNotFoundException(id);

    private async Task EnsureDirectionExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await agents.DirectionExistsAsync(id, cancellationToken))
            throw new AgentDirectionNotFoundException(id);
    }

    private async Task EnsureCodeAvailableAsync(string code, Guid? excludingId, CancellationToken cancellationToken)
    {
        if (await agents.CodeExistsAsync(code, excludingId, cancellationToken))
            throw new AgentDefinitionConflictException("An agent definition with this code already exists.");
    }

    private static AgentDefinitionDto ToDto(AgentDefinition agent) => new(
        agent.Id, agent.DirectionId, agent.Name, agent.Code, agent.Description, agent.Status,
        agent.ExecutionType, agent.CreatedAt, agent.UpdatedAt, agent.ArchivedAt, agent.Version);
}
