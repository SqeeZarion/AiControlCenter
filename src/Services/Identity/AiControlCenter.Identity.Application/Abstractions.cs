using AiControlCenter.Identity.Domain;

namespace AiControlCenter.Identity.Application;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<User> Items, int TotalCount)> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken);

    void Add(User user);
}

public interface IRoleRepository
{
    Task<IReadOnlyCollection<Role>> GetByNamesAsync(
        IReadOnlyCollection<RoleName> names,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Role>> ListAsync(CancellationToken cancellationToken);

    Task AcquireAdminMutationLockAsync(CancellationToken cancellationToken);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashForUpdateAsync(
        RefreshTokenHash hash,
        CancellationToken cancellationToken);

    void Add(RefreshToken refreshToken);

    Task RevokeAllAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    Task RevokeFamilyAsync(
        RefreshTokenFamilyId familyId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken);
}

public interface IIdentityUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

public enum PasswordVerificationResult
{
    Failed = 0,
    Success = 1,
    SuccessRehashNeeded = 2,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string passwordHash, string providedPassword);

    void VerifyUnknown(string providedPassword);
}

public interface IAccessTokenIssuer
{
    AccessTokenResult Issue(
        User user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset issuedAt);
}

public interface IRefreshTokenGenerator
{
    GeneratedRefreshToken Generate();

    RefreshTokenHash Hash(string rawToken);
}
