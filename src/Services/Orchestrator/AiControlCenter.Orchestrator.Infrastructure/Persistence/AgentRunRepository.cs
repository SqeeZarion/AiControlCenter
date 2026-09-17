using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence;

//читає та додає запуски в PostgreSQL.
internal sealed class AgentRunRepository(OrchestratorDbContext dbContext) : IAgentRunRepository
{
    public void Add(AgentRun run) => dbContext.AgentRuns.Add(run);

    public async Task<AgentRunPage> ListAsync(ListAgentRunsQuery query, CancellationToken cancellationToken)
    {
        var runs = dbContext.AgentRuns.AsNoTracking().AsQueryable();
        if (!query.CanViewAll) runs = runs.Where(run => run.OwnerUserId == query.RequestUserId);
        if (query.Status is not null) runs = runs.Where(run => run.Status == query.Status);
        if (query.AgentId is not null) runs = runs.Where(run => run.AgentId == query.AgentId);
        var total = await runs.CountAsync(cancellationToken);
        var skip = (long)(query.Page - 1) * query.PageSize;
        if (skip >= total) return new AgentRunPage([], total);
        var items = await runs.OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id)
            .Skip((int)skip).Take(query.PageSize).ToArrayAsync(cancellationToken);
        return new AgentRunPage(items, total);
    }

    public Task<AgentRun?> GetAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = dbContext.AgentRuns.Include(run => run.Steps).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return query.SingleOrDefaultAsync(run => run.Id == id, cancellationToken);
    }

    public Task<AgentRun?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.AgentRuns
            .FromSqlInterpolated($"SELECT *, xmin FROM orchestrator.agent_runs WHERE id = {id} FOR UPDATE")
            .Include(run => run.Steps)
            .SingleOrDefaultAsync(cancellationToken);
}

//зберігає зміни й керує транзакціями.
internal sealed class OrchestratorUnitOfWork(OrchestratorDbContext dbContext) : IOrchestratorUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AgentRunConflictException("Run was changed by another operation. Retry safely.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "ux_agent_runs_execution_message_id",
            })
        {
            throw new AgentRunConflictException(
                "Execution message is already assigned to another run.");
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
            return await action(cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await action(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
