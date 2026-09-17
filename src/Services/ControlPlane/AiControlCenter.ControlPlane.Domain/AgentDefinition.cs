using System.Text;
using System.Text.RegularExpressions;

namespace AiControlCenter.ControlPlane.Domain;

//статус агента
public enum AgentDefinitionStatus
{
    Inactive = 0,
    Active = 1,
}

//тип виконання
public enum AgentExecutionType
{
    Test = 0,
}

//обмеження заповнення полів
public sealed class AgentDefinition
{
    public const int NameMaxLength = 100;
    public const int CodeMaxLength = 64;
    public const int DescriptionMaxLength = 1000;

    private AgentDefinition()
    {
    }

    private AgentDefinition(
        Guid id,
        Guid directionId,
        string name,
        string code,
        string? description,
        AgentDefinitionStatus status,
        AgentExecutionType executionType,
        DateTimeOffset now)
    {
        Id = RequireId(id, nameof(id));
        DirectionId = RequireId(directionId, nameof(directionId));
        Name = NormalizeName(name);
        Code = NormalizeCode(code);
        Description = NormalizeDescription(description);
        Status = ValidateStatus(status);
        ExecutionType = ValidateExecutionType(executionType);
        CreatedAt = now.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public Guid DirectionId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public AgentDefinitionStatus Status { get; private set; }
    //Спосіб виконання
    public AgentExecutionType ExecutionType { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public uint Version { get; private set; }
    public bool IsArchived => ArchivedAt is not null;

    //створення агента
    public static AgentDefinition Create(
        Guid id,
        Guid directionId,
        string name,
        string code,
        string? description,
        AgentDefinitionStatus status,
        AgentExecutionType executionType,
        DateTimeOffset now) =>
        new(id, directionId, name, code, description, status, executionType, now);

    //оновлення агента
    public void Update(
        Guid directionId,
        string name,
        string code,
        string? description,
        AgentDefinitionStatus status,
        AgentExecutionType executionType,
        DateTimeOffset now)
    {
        EnsureNotArchived();
        var validatedDirectionId = RequireId(directionId, nameof(directionId));
        var normalizedName = NormalizeName(name);
        var normalizedCode = NormalizeCode(code);
        var normalizedDescription = NormalizeDescription(description);
        var validatedStatus = ValidateStatus(status);
        var validatedExecutionType = ValidateExecutionType(executionType);

        DirectionId = validatedDirectionId;
        Name = normalizedName;
        Code = normalizedCode;
        Description = normalizedDescription;
        Status = validatedStatus;
        ExecutionType = validatedExecutionType;
        Touch(now);
    }

    //зміна статусу
    public void ChangeStatus(AgentDefinitionStatus status, DateTimeOffset now)
    {
        EnsureNotArchived();
        Status = ValidateStatus(status);
        Touch(now);
    }

    //архівувати
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

    public static string NormalizeName(string? value)
    {
        var normalized = NormalizeUnicodeWhiteSpace(value);
        var scalarCount = normalized.EnumerateRunes().Count();
        if (scalarCount is < 2 or > NameMaxLength)
        {
            throw new ArgumentException(
                $"Agent name must contain 2-{NameMaxLength} Unicode scalar values.",
                nameof(value));
        }

        return normalized;
    }

    public static string NormalizeCode(string? value)
    {
        var normalized = Regex.Replace(value?.Trim().ToLowerInvariant() ?? string.Empty, @"[\s_]+", "-");
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
        if (normalized.Length is < 2 or > CodeMaxLength
            || !Regex.IsMatch(normalized, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                $"Agent code must contain 2-{CodeMaxLength} lowercase letters, digits or single hyphens.",
                nameof(value));
        }

        return normalized;
    }

    public static string? NormalizeDescription(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > DescriptionMaxLength)
        {
            throw new ArgumentException(
                $"Description cannot exceed {DescriptionMaxLength} characters.",
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

    private static Guid RequireId(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("A non-empty id is required.", name) : value;

    private static AgentDefinitionStatus ValidateStatus(AgentDefinitionStatus value) =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));

    private static AgentExecutionType ValidateExecutionType(AgentExecutionType value) =>
        value == AgentExecutionType.Test ? value : throw new ArgumentOutOfRangeException(nameof(value));

    private void EnsureNotArchived()
    {
        if (IsArchived)
        {
            throw new AgentDefinitionRuleViolationException(
                "Archived agent must be restored before it can be changed.");
        }
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();
}

public sealed class AgentDefinitionRuleViolationException(string message) : InvalidOperationException(message);
