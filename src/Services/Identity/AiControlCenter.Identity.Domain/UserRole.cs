namespace AiControlCenter.Identity.Domain;

public sealed class UserRole
{
    private UserRole()
    {
    }

    public UserRole(Guid userId, Guid roleId)
    {
        if (userId == Guid.Empty || roleId == Guid.Empty)
        {
            throw new ArgumentException("User and role ids are required.");
        }

        UserId = userId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public User User { get; private set; } = null!;

    public Role Role { get; private set; } = null!;
}
