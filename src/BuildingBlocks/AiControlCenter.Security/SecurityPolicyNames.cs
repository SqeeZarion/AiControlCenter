namespace AiControlCenter.Security;

//правило доступу до endpoint.

public static class SecurityPolicyNames
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string AdminOrDeveloper = nameof(AdminOrDeveloper);
    public const string AnyPlatformUser = nameof(AnyPlatformUser);
    public const string PasswordChanged = nameof(PasswordChanged);
}
