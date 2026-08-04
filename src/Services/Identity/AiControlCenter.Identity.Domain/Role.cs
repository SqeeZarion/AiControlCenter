namespace AiControlCenter.Identity.Domain;

public sealed class Role
{
    public static readonly Guid AdminId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid DeveloperId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public static readonly Guid UserId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private Role()
    {
    }

    private Role(Guid id, RoleName name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public Guid Id { get; private set; }

    public RoleName Name { get; private set; } = null!;

    public string Description { get; private set; } = string.Empty;

    public ICollection<UserRole> UserRoles { get; private set; } = [];

    public static IReadOnlyCollection<Role> SystemRoles { get; } =
    [
        new(AdminId, RoleName.Admin, "Full platform administration."),
        new(DeveloperId, RoleName.Developer, "Technical configuration and diagnostics."),
        new(UserId, RoleName.User, "Standard authenticated platform access."),
    ];
}
