using System.Text.RegularExpressions;
using System.Text;

namespace AiControlCenter.ControlPlane.Domain;

public enum DirectionStatus
{
    Inactive = 0,
    Active = 1,
}

public sealed class Direction
{
    public const int NameMaxLength = 100;
    public const int CodeMaxLength = 64;
    public const int DescriptionMaxLength = 1000;
    public const int IconMaxLength = 100;
    public const int SortOrderMax = 100_000;

    private Direction()
    {
    }

    private Direction(
        Guid id,
        string name,
        string code,
        string? description,
        string? icon,
        DirectionStatus status,
        int sortOrder,
        DateTimeOffset now)
    {
        Id = id;
        Name = NormalizeName(name);
        Code = NormalizeCode(code);
        Description = NormalizeOptional(description, DescriptionMaxLength, nameof(description));
        Icon = NormalizeOptional(icon, IconMaxLength, nameof(icon));
        Status = ValidateStatus(status);
        SortOrder = ValidateSortOrder(sortOrder);
        CreatedAt = now.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? Icon { get; private set; }

    public DirectionStatus Status { get; private set; }

    public int SortOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public uint Version { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public static Direction Create(
        Guid id,
        string name,
        string code,
        string? description,
        string? icon,
        DirectionStatus status,
        int sortOrder,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Direction id is required.", nameof(id));
        }

        return new Direction(id, name, code, description, icon, status, sortOrder, now);
    }

    public void Update(
        string name,
        string code,
        string? description,
        string? icon,
        DirectionStatus status,
        int sortOrder,
        DateTimeOffset now)
    {
        EnsureNotArchived();
        var normalizedName = NormalizeName(name);
        var normalizedCode = NormalizeCode(code);
        var normalizedDescription = NormalizeOptional(description, DescriptionMaxLength, nameof(description));
        var normalizedIcon = NormalizeOptional(icon, IconMaxLength, nameof(icon));
        var validatedStatus = ValidateStatus(status);
        var validatedSortOrder = ValidateSortOrder(sortOrder);

        Name = normalizedName;
        Code = normalizedCode;
        Description = normalizedDescription;
        Icon = normalizedIcon;
        Status = validatedStatus;
        SortOrder = validatedSortOrder;
        Touch(now);
    }

    public void ChangeStatus(DirectionStatus status, DateTimeOffset now)
    {
        EnsureNotArchived();
        Status = ValidateStatus(status);
        Touch(now);
    }

    public void ChangeSortOrder(int sortOrder, DateTimeOffset now)
    {
        EnsureNotArchived();
        SortOrder = ValidateSortOrder(sortOrder);
        Touch(now);
    }

    public bool Archive(DateTimeOffset now)
    {
        if (ArchivedAt is not null)
        {
            return false;
        }

        ArchivedAt = now.ToUniversalTime();
        UpdatedAt = ArchivedAt.Value;
        return true;
    }

    public bool Restore(DateTimeOffset now)
    {
        if (ArchivedAt is null)
        {
            return false;
        }

        ArchivedAt = null;
        Touch(now);
        return true;
    }

    public static string NormalizeName(string value)
    {
        var normalized = NormalizeUnicodeWhiteSpace(value);
        var scalarCount = normalized.EnumerateRunes().Count();
        if (scalarCount is < 2 or > NameMaxLength)
        {
            throw new ArgumentException(
                $"Direction name must contain 2-{NameMaxLength} Unicode scalar values.",
                nameof(value));
        }

        return normalized;
    }

    private static string NormalizeUnicodeWhiteSpace(string? value)
    {
        var normalized = new StringBuilder();
        var pendingSeparator = false;
        foreach (var rune in (value ?? string.Empty).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSeparator = normalized.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                normalized.Append(' ');
                pendingSeparator = false;
            }

            normalized.Append(rune);
        }

        return normalized.ToString();
    }

    public static string NormalizeCode(string value)
    {
        var normalized = Regex.Replace(value?.Trim().ToLowerInvariant() ?? string.Empty, @"[\s_]+", "-");
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
        if (normalized.Length is < 2 or > CodeMaxLength
            || !Regex.IsMatch(normalized, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                $"Direction code must contain 2-{CodeMaxLength} lowercase letters, digits or single hyphens.",
                nameof(value));
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    private static DirectionStatus ValidateStatus(DirectionStatus status) =>
        Enum.IsDefined(status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(status), "Unknown direction status.");

    private static int ValidateSortOrder(int sortOrder) =>
        sortOrder is >= 0 and <= SortOrderMax
            ? sortOrder
            : throw new ArgumentOutOfRangeException(
                nameof(sortOrder),
                $"Sort order must be between 0 and {SortOrderMax}.");

    private void EnsureNotArchived()
    {
        if (ArchivedAt is not null)
        {
            throw new DirectionRuleViolationException("Archived direction must be restored before it can be changed.");
        }
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();
}

public sealed class DirectionRuleViolationException(string message) : InvalidOperationException(message);
