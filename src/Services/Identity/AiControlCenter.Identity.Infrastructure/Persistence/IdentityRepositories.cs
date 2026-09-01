using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AiControlCenter.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        UsersWithRoles().SingleOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> GetByEmailForAuthenticationAsync(Email email, CancellationToken cancellationToken) =>
        UsersWithRoles().AsNoTracking().SingleOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        UsersWithRoles().SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> GetByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken) =>
        UsersWithRoles().AsNoTracking().SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Users
            .FromSqlInterpolated($"SELECT users.*, xmin FROM identity.users WHERE id = {id} FOR UPDATE")
            .Include(user => user.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<FailedLoginState?> RecordFailedLoginAsync(
        Guid userId,
        DateTimeOffset now,
        int maximumAttempts,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            command.CommandText =
                """
                UPDATE identity.users
                SET access_failed_count = access_failed_count + 1,
                    lockout_end = CASE
                        WHEN access_failed_count + 1 >= @maximum_attempts THEN @lockout_end
                        ELSE lockout_end
                    END,
                    updated_at = @now
                WHERE id = @user_id
                  AND status = 'Active'
                  AND (lockout_end IS NULL OR lockout_end <= @now)
                RETURNING access_failed_count, lockout_end;
                """;
            command.Parameters.AddWithValue("maximum_attempts", maximumAttempts);
            command.Parameters.AddWithValue("lockout_end", now.Add(lockoutDuration));
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("user_id", userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new FailedLoginState(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<(IReadOnlyCollection<User> Items, int TotalCount)> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var totalCount = await dbContext.Users.CountAsync(cancellationToken);
        var items = await UsersWithRoles()
            .AsNoTracking()
            .OrderBy(user => user.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
        return (items, totalCount);
    }

    public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken) =>
        dbContext.Users.CountAsync(
            user => user.Status == UserStatus.Active
                && user.UserRoles.Any(userRole => userRole.RoleId == Role.AdminId),
            cancellationToken);

    public void Add(User user) => dbContext.Users.Add(user);

    private IQueryable<User> UsersWithRoles() => dbContext.Users
        .Include(user => user.UserRoles)
        .ThenInclude(userRole => userRole.Role);
}

internal sealed class RoleRepository(IdentityDbContext dbContext) : IRoleRepository
{
    public async Task<IReadOnlyCollection<Role>> GetByNamesAsync(
        IReadOnlyCollection<RoleName> names,
        CancellationToken cancellationToken)
    {
        var ids = names.Select(ToId).Distinct().ToArray();
        return await dbContext.Roles.Where(role => ids.Contains(role.Id)).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<Role>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.Roles.AsNoTracking().OrderBy(role => role.Name).ToArrayAsync(cancellationToken);

    public async Task AcquireAdminMutationLockAsync(CancellationToken cancellationToken)
    {
        _ = await dbContext.Roles
            .FromSqlInterpolated($"SELECT * FROM identity.roles WHERE id = {Role.AdminId} FOR UPDATE")
            .SingleAsync(cancellationToken);
    }

    private static Guid ToId(RoleName name) => name.Value switch
    {
        "Admin" => Role.AdminId,
        "Developer" => Role.DeveloperId,
        "User" => Role.UserId,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}

internal sealed class RefreshSessionRepository(IdentityDbContext dbContext) : IRefreshSessionRepository
{
    public async Task<LockedRefreshSession?> GetByTokenHashForUpdateAsync(
        RefreshTokenHash hash,
        CancellationToken cancellationToken)
    {
        var sessionId = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(token => token.TokenHash == hash)
            .Select(token => (Guid?)token.SessionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (sessionId is null)
        {
            return null;
        }

        var session = await dbContext.RefreshSessions
            .FromSqlInterpolated(
                $"SELECT refresh_sessions.*, xmin FROM identity.refresh_sessions WHERE id = {sessionId.Value} FOR UPDATE")
            .Include(value => value.User)
            .ThenInclude(user => user.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleAsync(cancellationToken);
        var token = await dbContext.RefreshTokens
            .SingleAsync(value => value.TokenHash == hash, cancellationToken);
        return new LockedRefreshSession(session, token);
    }

    public void Add(RefreshSession refreshSession) => dbContext.RefreshSessions.Add(refreshSession);

    public void Add(RefreshToken refreshToken) => dbContext.RefreshTokens.Add(refreshToken);

    public Task RevokeAllAsync(
        Guid userId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken) =>
        dbContext.RefreshSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAt, now)
                .SetProperty(session => session.RevokedReason, reason),
                cancellationToken);
}

internal sealed class IdentityUnitOfWork(IdentityDbContext dbContext) : IIdentityUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
