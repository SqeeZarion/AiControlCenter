using AiControlCenter.Orchestrator.Domain;

namespace AiControlCenter.Orchestrator.Application;

public interface IAgentRunRepository
{
    void Add(AgentRun run);
    Task<AgentRunPage> ListAsync(ListAgentRunsQuery query, CancellationToken cancellationToken);
    Task<AgentRun?> GetAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    Task<AgentRun?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record AgentRunPage(IReadOnlyCollection<AgentRun> Items, int TotalCount);

public interface IOrchestratorUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}

public interface IAgentCatalogClient
{
    Task<AgentSnapshot> GetRunnableAgentAsync(
        Guid agentId,
        string delegatedAuthorization,
        CancellationToken cancellationToken);
}

public interface IRunTransport
{
    Task QueueAsync(AgentRun run, Guid messageId, CancellationToken cancellationToken);
    Task PublishStatusAsync(
        AgentRun run,
        RunStep? runStep,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}
