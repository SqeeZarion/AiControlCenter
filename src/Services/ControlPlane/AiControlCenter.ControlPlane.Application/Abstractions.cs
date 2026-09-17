using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.Application;

public interface IDirectionRepository
{
    Task<DirectionPage> ListAsync(
        ListDirectionsQuery query,
        CancellationToken cancellationToken);

    Task<Direction?> GetByIdAsync(Guid id, bool tracking, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken);

    void Add(Direction direction);
}

public interface IAgentDefinitionRepository
{
    Task<AgentDefinitionPage> ListAsync(ListAgentDefinitionsQuery query, CancellationToken cancellationToken);
    Task<AgentDefinition?> GetByIdAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken);
    Task<bool> DirectionExistsAsync(Guid directionId, CancellationToken cancellationToken);
    Task<RunnableAgentLookup> GetRunnableSnapshotAsync(Guid id, CancellationToken cancellationToken);
    void Add(AgentDefinition agent);
}

public sealed record AgentDefinitionPage(IReadOnlyCollection<AgentDefinition> Items, int TotalCount);

public sealed record RunnableAgentSnapshot(
    Guid AgentId,
    string AgentName,
    string AgentCode,
    string? AgentDescription,
    uint AgentVersion,
    AgentExecutionType ExecutionType,
    Guid DirectionId,
    string DirectionName,
    string DirectionCode,
    uint DirectionVersion);

public enum RunnableAgentLookupStatus
{
    Missing,
    NotRunnable,
    Runnable,
}

public sealed record RunnableAgentLookup(
    RunnableAgentLookupStatus Status,
    RunnableAgentSnapshot? Snapshot);

public sealed record DirectionPage(IReadOnlyCollection<Direction> Items, int TotalCount);

public interface IControlPlaneUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
