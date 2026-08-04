using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        UsersWithRoles().SingleOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        UsersWithRoles().SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

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

internal sealed class RefreshTokenRepository(IdentityDbContext dbContext) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashForUpdateAsync(
        RefreshTokenHash hash,
        CancellationToken cancellationToken) =>
        dbContext.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM identity.refresh_tokens WHERE token_hash = {hash.Value} FOR UPDATE")
            .Include(token => token.User)
            .ThenInclude(user => user.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(RefreshToken refreshToken) => dbContext.RefreshTokens.Add(refreshToken);

    public Task RevokeAllAsync(
        Guid userId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken) =>
        dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAt, now)
                .SetProperty(token => token.RevocationReason, reason),
                cancellationToken);

    public Task RevokeFamilyAsync(
        RefreshTokenFamilyId familyId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken) =>
        dbContext.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAt, now)
                .SetProperty(token => token.RevocationReason, reason),
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
