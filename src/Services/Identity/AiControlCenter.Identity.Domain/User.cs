namespace AiControlCenter.Identity.Domain;

public sealed class User
{
    private User()
    {
    }

    private User(
        Guid id,
        Email email,
        DisplayName displayName,
        string passwordHash,
        bool mustChangePassword,
        DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = RequirePasswordHash(passwordHash);
        MustChangePassword = mustChangePassword;
        Status = UserStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Email Email { get; private set; } = null!;

    public DisplayName DisplayName { get; private set; } = null!;

    public string PasswordHash { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    //Показує, чи повинен користувач змінити тимчасовий пароль.
    public bool MustChangePassword { get; private set; }

    public int AccessFailedCount { get; private set; }

    public DateTimeOffset? LockoutEnd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public uint Version { get; private set; }

    public ICollection<UserRole> UserRoles { get; private set; } = [];

    public ICollection<RefreshToken> RefreshTokens { get; private set; } = [];

    public static User Create(
        Email email,
        DisplayName displayName,
        string passwordHash,
        bool mustChangePassword,
        DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), email, displayName, passwordHash, mustChangePassword, createdAt);

    //Перевірка можливості входу
    public bool CanLogin(DateTimeOffset now) =>
        Status == UserStatus.Active && (LockoutEnd is null || LockoutEnd <= now);

    //Невдала спроба входу
    public void RecordFailedLogin(DateTimeOffset now, int maximumAttempts, TimeSpan lockoutDuration)
    {
        //потрібен, щоб порахувати неправильні введення пароля й тимчасово заблокувати вхід після заданої кількості помилок.
        AccessFailedCount++;
        UpdatedAt = now;
        if (AccessFailedCount >= maximumAttempts)
        {
            LockoutEnd = now.Add(lockoutDuration);
        }
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        AccessFailedCount = 0;
        LockoutEnd = null;
        LastLoginAt = now;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        Status = UserStatus.Active;
        AccessFailedCount = 0;
        LockoutEnd = null;
        UpdatedAt = now;
    }

    //блкує користувача
    public void Block(DateTimeOffset now)
    {
        Status = UserStatus.Blocked;
        UpdatedAt = now;
    }

    //зміна пароля
    public void ChangePassword(string passwordHash, bool mustChangePassword, DateTimeOffset now)
    {
        PasswordHash = RequirePasswordHash(passwordHash);
        MustChangePassword = mustChangePassword;
        AccessFailedCount = 0;
        LockoutEnd = null;
        UpdatedAt = now;
    }

    //зміна ролей
    public void ReplaceRoles(IEnumerable<Role> roles, DateTimeOffset now)
    {
        //отримуєм унікальний айді користувача
        var roleIds = roles.Select(role => role.Id).Distinct().ToArray();
        if (roleIds.Length == 0)
        {
            throw new InvalidOperationException("At least one role is required.");
        }

        UserRoles.Clear();
        foreach (var roleId in roleIds)
        {
            UserRoles.Add(new UserRole(Id, roleId));
        }

        UpdatedAt = now;
    }

    //
    public bool HasRole(Guid roleId) => UserRoles.Any(userRole => userRole.RoleId == roleId);

    //хешування пароля
    private static string RequirePasswordHash(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Password hash is required.", nameof(value))
            : value;
}
