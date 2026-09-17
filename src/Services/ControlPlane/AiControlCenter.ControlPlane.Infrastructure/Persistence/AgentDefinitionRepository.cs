using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

//Він відповідає за список, пошук, отримання агента, перевірку унікальності коду та отримання агента, якого можна запустити.
internal sealed class AgentDefinitionRepository(ControlPlaneDbContext dbContext) : IAgentDefinitionRepository
{
    public async Task<AgentDefinitionPage> ListAsync(
        ListAgentDefinitionsQuery query,
        CancellationToken cancellationToken)
    {
        var agents = dbContext.AgentDefinitions.AsNoTracking().AsQueryable();
        if (!query.IncludeArchived) agents = agents.Where(agent => agent.ArchivedAt == null);
        if (query.Status is not null) agents = agents.Where(agent => agent.Status == query.Status);
        if (query.DirectionId is not null) agents = agents.Where(agent => agent.DirectionId == query.DirectionId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLikePattern(query.Search.Trim())}%";
            agents = agents.Where(agent =>
                EF.Functions.ILike(agent.Name, pattern, "\\")
                || EF.Functions.ILike(agent.Code, pattern, "\\")
                || (agent.Description != null && EF.Functions.ILike(agent.Description, pattern, "\\")));
        }

        var total = await agents.CountAsync(cancellationToken);
        var skip = (long)(query.Page - 1) * query.PageSize;
        if (skip >= total) return new AgentDefinitionPage([], total);
        var items = await agents.OrderBy(agent => agent.Name).ThenBy(agent => agent.Id)
            .Skip((int)skip).Take(query.PageSize).ToArrayAsync(cancellationToken);
        return new AgentDefinitionPage(items, total);
    }

    public Task<AgentDefinition?> GetByIdAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = dbContext.AgentDefinitions.AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return query.SingleOrDefaultAsync(agent => agent.Id == id, cancellationToken);
    }

    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.AgentDefinitions.AnyAsync(
            agent => agent.Code == code && (excludingId == null || agent.Id != excludingId),
            cancellationToken);

    public Task<bool> DirectionExistsAsync(Guid directionId, CancellationToken cancellationToken) =>
        dbContext.Directions.AnyAsync(direction => direction.Id == directionId, cancellationToken);

    public async Task<RunnableAgentLookup> GetRunnableSnapshotAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var candidate = await (
            from agent in dbContext.AgentDefinitions.AsNoTracking()
            join direction in dbContext.Directions.AsNoTracking() on agent.DirectionId equals direction.Id
            where agent.Id == id
            select new
            {
                Agent = agent,
                Direction = direction,
            }).SingleOrDefaultAsync(cancellationToken);

        if (candidate is null)
            return new RunnableAgentLookup(RunnableAgentLookupStatus.Missing, null);

        if (candidate.Agent.Status != AgentDefinitionStatus.Active
            || candidate.Agent.ArchivedAt is not null
            || candidate.Direction.Status != DirectionStatus.Active
            || candidate.Direction.ArchivedAt is not null)
            return new RunnableAgentLookup(RunnableAgentLookupStatus.NotRunnable, null);

        return new RunnableAgentLookup(
            RunnableAgentLookupStatus.Runnable,
            new RunnableAgentSnapshot(
                candidate.Agent.Id,
                candidate.Agent.Name,
                candidate.Agent.Code,
                candidate.Agent.Description,
                candidate.Agent.Version,
                candidate.Agent.ExecutionType,
                candidate.Direction.Id,
                candidate.Direction.Name,
                candidate.Direction.Code,
                candidate.Direction.Version));
    }

    public void Add(AgentDefinition agent) => dbContext.AgentDefinitions.Add(agent);

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
