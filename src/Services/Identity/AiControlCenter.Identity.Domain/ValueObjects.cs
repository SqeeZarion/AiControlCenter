using System.Net.Mail;

namespace AiControlCenter.Identity.Domain;

public sealed record Email
{
    private Email(string value) => Value = value;

    public string Value { get; private init; }

    public static Email Create(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 320 || !MailAddress.TryCreate(normalized, out _))
        {
            throw new ArgumentException("A valid email address is required.", nameof(value));
        }

        return new Email(normalized);
    }

    public override string ToString() => Value;
}

public sealed record DisplayName
{
    private DisplayName(string value) => Value = value;

    public string Value { get; private init; }

    public static DisplayName Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 2 or > 100)
        {
            throw new ArgumentException("Display name must contain between 2 and 100 characters.", nameof(value));
        }

        return new DisplayName(normalized);
    }

    public override string ToString() => Value;
}

public sealed record RoleName
{
    private RoleName(string value) => Value = value;

    public string Value { get; private init; }

    public static RoleName Admin { get; } = new("Admin");

    public static RoleName Developer { get; } = new("Developer");

    public static RoleName User { get; } = new("User");

    public static RoleName Create(string value) => value.Trim() switch
    {
        "Admin" => Admin,
        "Developer" => Developer,
        "User" => User,
        _ => throw new ArgumentException("Unknown system role.", nameof(value)),
    };

    public override string ToString() => Value;
}

public sealed record RefreshTokenHash
{
    private RefreshTokenHash(string value) => Value = value;

    public string Value { get; private init; }

    public static RefreshTokenHash Create(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Refresh token hash must be a SHA-256 hexadecimal value.", nameof(value));
        }

        return new RefreshTokenHash(normalized);
    }

    public override string ToString() => Value;
}

public readonly record struct RefreshTokenFamilyId
{
    public RefreshTokenFamilyId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Refresh token family id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static RefreshTokenFamilyId New() => new(Guid.NewGuid());
}
