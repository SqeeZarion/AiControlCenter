using AiControlCenter.Identity.Domain;

namespace AiControlCenter.Identity.Application;

public sealed record LoginRequest(string Email, string Password);

public sealed record CreateUserRequest(
    string Email,
    string DisplayName,
    string TemporaryPassword,
    IReadOnlyCollection<string> Roles);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetUserPasswordRequest(string TemporaryPassword);

public sealed record ReplaceUserRolesRequest(IReadOnlyCollection<string> Roles);

public sealed record ChangeUserStatusRequest(UserStatus Status);

public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    bool MustChangePassword);

public sealed record UserDetailsDto(
    Guid Id,
    string Email,
    string DisplayName,
    UserStatus Status,
    IReadOnlyCollection<string> Roles,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record RoleDto(Guid Id, string Name, string Description);

public sealed record UserListDto(
    IReadOnlyCollection<UserDetailsDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);

public sealed record AuthSessionResult(
    AccessTokenResult AccessToken,
    CurrentUserDto User,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record GeneratedRefreshToken(string RawToken, RefreshTokenHash Hash);
