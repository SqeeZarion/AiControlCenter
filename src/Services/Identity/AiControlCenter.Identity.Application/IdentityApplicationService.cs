using AiControlCenter.Identity.Domain;

namespace AiControlCenter.Identity.Application;

public sealed class IdentityApplicationService(
    IUserRepository users,
    IRoleRepository roles,
    IRefreshTokenRepository refreshTokens,
    IIdentityUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer accessTokenIssuer,
    IRefreshTokenGenerator refreshTokenGenerator,
    TimeProvider timeProvider)
{
    private const int MaximumLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshFamilyLifetime = TimeSpan.FromDays(30);

    public async Task<AuthSessionResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var user = await users.GetByEmailAsync(Email.Create(request.Email), cancellationToken);
        if (user is null)
        {
            passwordHasher.VerifyUnknown(request.Password);
            throw new IdentityAuthenticationException();
        }

        if (!user.CanLogin(now))
        {
            throw new IdentityAuthenticationException();
        }

        var verification = passwordHasher.Verify(user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.RecordFailedLogin(now, MaximumLoginAttempts, LockoutDuration);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new IdentityAuthenticationException();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.ChangePassword(passwordHasher.Hash(request.Password), user.MustChangePassword, now);
        }

        user.RecordSuccessfulLogin(now);
        return await CreateSessionAsync(user, RefreshTokenFamilyId.New(), now, cancellationToken);
    }

    public async Task<AuthSessionResult> RefreshSessionAsync(
        string rawRefreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new IdentityAuthenticationException();
        }

        var outcome = await unitOfWork.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            var now = timeProvider.GetUtcNow();
            var hash = refreshTokenGenerator.Hash(rawRefreshToken);
            var current = await refreshTokens.GetByHashForUpdateAsync(hash, transactionCancellationToken);
            if (current is null)
            {
                return RefreshOutcome.Invalid;
            }

            if (current.ReplacedByTokenId is not null)
            {
                await refreshTokens.RevokeFamilyAsync(
                    current.FamilyId,
                    now,
                    "Refresh token reuse detected",
                    transactionCancellationToken);
                await unitOfWork.SaveChangesAsync(transactionCancellationToken);
                return RefreshOutcome.ReuseDetected;
            }

            if (!current.IsActive(now) || !current.User.CanLogin(now))
            {
                await refreshTokens.RevokeFamilyAsync(
                    current.FamilyId,
                    now,
                    "Session is no longer valid",
                    transactionCancellationToken);
                await unitOfWork.SaveChangesAsync(transactionCancellationToken);
                return RefreshOutcome.Invalid;
            }

            var generated = refreshTokenGenerator.Generate();
            var replacement = RefreshToken.Create(
                current.UserId,
                generated.Hash,
                current.FamilyId,
                now,
                current.ExpiresAt);
            current.RotateTo(replacement, now);
            refreshTokens.Add(replacement);
            await unitOfWork.SaveChangesAsync(transactionCancellationToken);

            return new RefreshOutcome(
                BuildSession(current.User, generated.RawToken, replacement.ExpiresAt, now),
                false);
        }, cancellationToken);

        return outcome.Session ?? throw new IdentityAuthenticationException();
    }

    public async Task LogoutCurrentSessionAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return;
        }

        await unitOfWork.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            var token = await refreshTokens.GetByHashForUpdateAsync(
                refreshTokenGenerator.Hash(rawRefreshToken),
                transactionCancellationToken);
            token?.Revoke(timeProvider.GetUtcNow(), "User logout");
            await unitOfWork.SaveChangesAsync(transactionCancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task LogoutAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await refreshTokens.RevokeAllAsync(
            userId,
            timeProvider.GetUtcNow(),
            "User logout from all sessions",
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<CurrentUserDto> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        return ToCurrentUser(user);
    }

    public async Task ChangeOwnPasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        if (passwordHasher.Verify(user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new IdentityAuthenticationException();
        }

        var now = timeProvider.GetUtcNow();
        user.ChangePassword(passwordHasher.Hash(request.NewPassword), false, now);
        await refreshTokens.RevokeAllAsync(user.Id, now, "Password changed", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<UserDetailsDto> CreateUserAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var email = Email.Create(request.Email);
        if (await users.GetByEmailAsync(email, cancellationToken) is not null)
        {
            throw new IdentityConflictException("A user with this email already exists.");
        }

        var assignedRoles = await ResolveRolesAsync(request.Roles, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var user = User.Create(
            email,
            DisplayName.Create(request.DisplayName),
            passwordHasher.Hash(request.TemporaryPassword),
            true,
            now);
        user.ReplaceRoles(assignedRoles, now);
        users.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDetails(user);
    }

    public async Task<UserListDto> ListUsersAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await users.ListAsync(page, pageSize, cancellationToken);
        return new UserListDto(result.Items.Select(ToDetails).ToArray(), page, pageSize, result.TotalCount);
    }

    public async Task<UserDetailsDto> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
        ToDetails(await GetRequiredUserAsync(userId, cancellationToken));

    public async Task<UserDetailsDto> ActivateUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        user.Activate(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDetails(user);
    }

    public Task<UserDetailsDto> BlockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await roles.AcquireAdminMutationLockAsync(transactionCancellationToken);
            var user = await GetRequiredUserAsync(userId, transactionCancellationToken);
            if (user.Status == UserStatus.Active && user.HasRole(Role.AdminId)
                && await users.CountActiveAdminsAsync(transactionCancellationToken) <= 1)
            {
                throw new IdentityConflictException("The last active Admin cannot be blocked.");
            }

            var now = timeProvider.GetUtcNow();
            user.Block(now);
            await refreshTokens.RevokeAllAsync(user.Id, now, "User blocked", transactionCancellationToken);
            await unitOfWork.SaveChangesAsync(transactionCancellationToken);
            return ToDetails(user);
        }, cancellationToken);

    public Task<UserDetailsDto> ReplaceUserRolesAsync(
        Guid userId,
        ReplaceUserRolesRequest request,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await roles.AcquireAdminMutationLockAsync(transactionCancellationToken);
            var user = await GetRequiredUserAsync(userId, transactionCancellationToken);
            var assignedRoles = await ResolveRolesAsync(request.Roles, transactionCancellationToken);
            var removesAdmin = user.HasRole(Role.AdminId) && assignedRoles.All(role => role.Id != Role.AdminId);
            if (user.Status == UserStatus.Active && removesAdmin
                && await users.CountActiveAdminsAsync(transactionCancellationToken) <= 1)
            {
                throw new IdentityConflictException("The last active Admin must retain the Admin role.");
            }

            var now = timeProvider.GetUtcNow();
            user.ReplaceRoles(assignedRoles, now);
            await refreshTokens.RevokeAllAsync(user.Id, now, "Roles changed", transactionCancellationToken);
            await unitOfWork.SaveChangesAsync(transactionCancellationToken);
            return ToDetails(user);
        }, cancellationToken);

    public async Task ResetUserPasswordAsync(
        Guid userId,
        ResetUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        user.ChangePassword(passwordHasher.Hash(request.TemporaryPassword), true, now);
        await refreshTokens.RevokeAllAsync(user.Id, now, "Password reset by Admin", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeUserSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        _ = await GetRequiredUserAsync(userId, cancellationToken);
        await refreshTokens.RevokeAllAsync(
            userId,
            timeProvider.GetUtcNow(),
            "Sessions revoked by Admin",
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<RoleDto>> ListRolesAsync(CancellationToken cancellationToken) =>
        (await roles.ListAsync(cancellationToken))
        .Select(role => new RoleDto(role.Id, role.Name.Value, role.Description))
        .ToArray();

    public Task<bool> BootstrapAdminAsync(
        string emailValue,
        string displayName,
        string temporaryPassword,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await roles.AcquireAdminMutationLockAsync(transactionCancellationToken);
            if (await users.CountActiveAdminsAsync(transactionCancellationToken) > 0)
            {
                return false;
            }

            var adminRole = (await roles.GetByNamesAsync([RoleName.Admin], transactionCancellationToken)).Single();
            var email = Email.Create(emailValue);
            var now = timeProvider.GetUtcNow();
            var user = await users.GetByEmailAsync(email, transactionCancellationToken);
            if (user is null)
            {
                user = User.Create(
                    email,
                    DisplayName.Create(displayName),
                    passwordHasher.Hash(temporaryPassword),
                    true,
                    now);
                users.Add(user);
            }
            else
            {
                user.Activate(now);
                user.ChangePassword(passwordHasher.Hash(temporaryPassword), true, now);
            }

            user.ReplaceRoles([adminRole], now);
            await refreshTokens.RevokeAllAsync(user.Id, now, "Admin bootstrap", transactionCancellationToken);
            await unitOfWork.SaveChangesAsync(transactionCancellationToken);
            return true;
        }, cancellationToken);

    private async Task<AuthSessionResult> CreateSessionAsync(
        User user,
        RefreshTokenFamilyId familyId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var generated = refreshTokenGenerator.Generate();
        var expiresAt = now.Add(RefreshFamilyLifetime);
        refreshTokens.Add(RefreshToken.Create(user.Id, generated.Hash, familyId, now, expiresAt));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return BuildSession(user, generated.RawToken, expiresAt, now);
    }

    private AuthSessionResult BuildSession(
        User user,
        string rawRefreshToken,
        DateTimeOffset refreshExpiresAt,
        DateTimeOffset now)
    {
        var roleNames = GetRoleNames(user);
        return new AuthSessionResult(
            accessTokenIssuer.Issue(user, roleNames, now),
            ToCurrentUser(user),
            rawRefreshToken,
            refreshExpiresAt);
    }

    private async Task<User> GetRequiredUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await users.GetByIdAsync(userId, cancellationToken)
        ?? throw new IdentityNotFoundException("User");

    private async Task<IReadOnlyCollection<Role>> ResolveRolesAsync(
        IReadOnlyCollection<string> requestedRoles,
        CancellationToken cancellationToken)
    {
        var names = requestedRoles.Select(RoleName.Create).Distinct().ToArray();
        var resolved = await roles.GetByNamesAsync(names, cancellationToken);
        if (resolved.Count != names.Length)
        {
            throw new IdentityConflictException("One or more roles are invalid.");
        }

        return resolved;
    }

    private static CurrentUserDto ToCurrentUser(User user) => new(
        user.Id,
        user.Email.Value,
        user.DisplayName.Value,
        GetRoleNames(user),
        user.MustChangePassword);

    private static UserDetailsDto ToDetails(User user) => new(
        user.Id,
        user.Email.Value,
        user.DisplayName.Value,
        user.Status,
        GetRoleNames(user),
        user.MustChangePassword,
        user.CreatedAt,
        user.LastLoginAt);

    private static string[] GetRoleNames(User user) =>
        user.UserRoles.Select(userRole => userRole.Role.Name.Value).Order().ToArray();

    private sealed record RefreshOutcome(AuthSessionResult? Session, bool Reused)
    {
        public static RefreshOutcome Invalid { get; } = new(null, false);

        public static RefreshOutcome ReuseDetected { get; } = new(null, true);
    }
}
