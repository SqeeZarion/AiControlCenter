using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.Extensions.Time.Testing;

namespace AiControlCenter.Identity.UnitTests;

public sealed class LastAdminTests
{
    [Fact]
    public async Task LastActiveAdminCannotBeBlocked()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var admin = User.Create(
            Email.Create("admin@example.com"),
            DisplayName.Create("Admin User"),
            "hash",
            false,
            now);
        admin.ReplaceRoles([Role.SystemRoles.Single(role => role.Id == Role.AdminId)], now);
        var users = new UserRepositoryStub(admin, 1);
        var service = new IdentityApplicationService(
            users,
            new RoleRepositoryStub(),
            new RefreshRepositoryStub(),
            new UnitOfWorkStub(),
            new PasswordHasherStub(),
            new AccessTokenIssuerStub(),
            new RefreshTokenGeneratorStub(),
            new FakeTimeProvider(now));

        var exception = await Assert.ThrowsAsync<IdentityConflictException>(() =>
            service.BlockUserAsync(admin.Id, CancellationToken.None));

        Assert.Contains("last active Admin", exception.Message, StringComparison.Ordinal);
        Assert.Equal(UserStatus.Active, admin.Status);
    }

    private sealed class UserRepositoryStub(User user, int adminCount) : IUserRepository
    {
        public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) => Task.FromResult<User?>(user);
        public Task<User?> GetByEmailForAuthenticationAsync(Email email, CancellationToken cancellationToken) => Task.FromResult<User?>(user);
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<User?>(user);
        public Task<User?> GetByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<User?>(user);
        public Task<User?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<User?>(user);
        public Task<FailedLoginState?> RecordFailedLoginAsync(Guid userId, DateTimeOffset current, int maximumAttempts, TimeSpan lockoutDuration, CancellationToken cancellationToken) =>
            Task.FromResult<FailedLoginState?>(new FailedLoginState(1, null));
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> ListAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(((IReadOnlyCollection<User>)[user], 1));
        public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken) => Task.FromResult(adminCount);
        public void Add(User value) { }
    }

    private sealed class RoleRepositoryStub : IRoleRepository
    {
        public Task<IReadOnlyCollection<Role>> GetByNamesAsync(IReadOnlyCollection<RoleName> names, CancellationToken cancellationToken) =>
            Task.FromResult(Role.SystemRoles);
        public Task<IReadOnlyCollection<Role>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Role.SystemRoles);
        public Task AcquireAdminMutationLockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RefreshRepositoryStub : IRefreshSessionRepository
    {
        public Task<LockedRefreshSession?> GetByTokenHashForUpdateAsync(RefreshTokenHash hash, CancellationToken cancellationToken) => Task.FromResult<LockedRefreshSession?>(null);
        public void Add(RefreshSession refreshSession) { }
        public void Add(RefreshToken refreshToken) { }
        public Task RevokeAllAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnitOfWorkStub : IIdentityUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed class PasswordHasherStub : IPasswordHasher
    {
        public string Hash(string password) => "hash";
        public PasswordVerificationResult Verify(string passwordHash, string providedPassword) => PasswordVerificationResult.Success;
        public void VerifyUnknown(string providedPassword) { }
    }

    private sealed class AccessTokenIssuerStub : IAccessTokenIssuer
    {
        public AccessTokenResult Issue(User user, IReadOnlyCollection<string> roles, DateTimeOffset issuedAt) => new("token", issuedAt.AddMinutes(10));
    }

    private sealed class RefreshTokenGeneratorStub : IRefreshTokenGenerator
    {
        public GeneratedRefreshToken Generate() => new("raw", RefreshTokenHash.Create(new string('A', 64)));
        public RefreshTokenHash Hash(string rawToken) => RefreshTokenHash.Create(new string('A', 64));
    }
}
