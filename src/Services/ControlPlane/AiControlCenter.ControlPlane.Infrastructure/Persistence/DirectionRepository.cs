using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

internal sealed class DirectionRepository(ControlPlaneDbContext dbContext) : IDirectionRepository
{
    public async Task<DirectionPage> ListAsync(
        ListDirectionsQuery query,
        CancellationToken cancellationToken)
    {
        var directions = dbContext.Directions.AsNoTracking().AsQueryable();
        if (!query.IncludeArchived)
        {
            directions = directions.Where(direction => direction.ArchivedAt == null);
        }

        if (query.Status is not null)
        {
            directions = directions.Where(direction => direction.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLikePattern(query.Search.Trim())}%";
            directions = directions.Where(direction => EF.Functions.ILike(direction.Name, pattern, "\\"));
        }

        var totalCount = await directions.CountAsync(cancellationToken);
        var skip = (long)(query.Page - 1) * query.PageSize;
        if (skip >= totalCount)
        {
            return new DirectionPage([], totalCount);
        }

        var items = await directions
            .OrderBy(direction => direction.SortOrder)
            .ThenBy(direction => direction.Name)
            .ThenBy(direction => direction.Id)
            .Skip((int)skip)
            .Take(query.PageSize)
            .ToArrayAsync(cancellationToken);
        return new DirectionPage(items, totalCount);
    }

    public Task<Direction?> GetByIdAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = dbContext.Directions.AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.SingleOrDefaultAsync(direction => direction.Id == id, cancellationToken);
    }

    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Directions.AnyAsync(
            direction => direction.Code == code && (excludingId == null || direction.Id != excludingId),
            cancellationToken);

    public void Add(Direction direction) => dbContext.Directions.Add(direction);

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

internal sealed class ControlPlaneUnitOfWork(ControlPlaneDbContext dbContext) : IControlPlaneUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (dbContext.ChangeTracker.Entries<AgentDefinition>().Any())
            {
                throw new AgentDefinitionConflictException(
                    "Agent definition was changed by another request. Reload and retry.");
            }

            throw new DirectionConflictException(
                "Direction was changed by another request. Reload and retry.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            if (dbContext.ChangeTracker.Entries<AgentDefinition>().Any())
            {
                throw new AgentDefinitionConflictException(
                    "An agent definition with this code already exists.");
            }

            throw new DirectionConflictException("A direction with this code already exists.");
        }
    }
}
