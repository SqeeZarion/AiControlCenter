using AiControlCenter.Identity.Domain;

namespace AiControlCenter.Identity.Application;

//IUserRepository описує, що Application може робити з користувачами.
public interface IUserRepository
{
    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<User?> GetByEmailForAuthenticationAsync(Email email, CancellationToken cancellationToken);

    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<User?> GetByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken);

    Task<User?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<FailedLoginState?> RecordFailedLoginAsync(
        Guid userId,
        DateTimeOffset now,
        int maximumAttempts,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken);

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

public interface IRefreshSessionRepository
{
    Task<LockedRefreshSession?> GetByTokenHashForUpdateAsync(
        RefreshTokenHash hash,
        CancellationToken cancellationToken);

    void Add(RefreshSession refreshSession);

    void Add(RefreshToken refreshToken);

    Task RevokeAllAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
}

//керує збереженням змін та транзакціями.
public interface IIdentityUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

//перевірка паролів
public enum PasswordVerificationResult
{
    Failed = 0,
    Success = 1,
    SuccessRehashNeeded = 2,
}

//Перетворює пароль на hash для збереження в БД.
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string passwordHash, string providedPassword);

    void VerifyUnknown(string providedPassword);
}

//Створення access-токена
public interface IAccessTokenIssuer
{
    AccessTokenResult Issue(
        User user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset issuedAt);
}

//Створення refresh-токена
public interface IRefreshTokenGenerator
{
    GeneratedRefreshToken Generate();

    RefreshTokenHash Hash(string rawToken);
}
