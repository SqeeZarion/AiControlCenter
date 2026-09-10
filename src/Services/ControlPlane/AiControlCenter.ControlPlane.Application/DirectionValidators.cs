using System.Linq.Expressions;
using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Application;

public sealed class ListDirectionsQueryValidator : AbstractValidator<ListDirectionsQuery>
{
    public ListDirectionsQueryValidator()
    {
        RuleFor(query => query.Status).Must(status => status is null || Enum.IsDefined(status.Value));
        RuleFor(query => query.Search).MaximumLength(Direction.NameMaxLength);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListDirectionsQuery.MaximumPageSize);
    }
}

public sealed class GetDirectionQueryValidator : AbstractValidator<GetDirectionQuery>
{
    public GetDirectionQueryValidator() => RuleFor(query => query.Id).NotEmpty();
}

public sealed class CreateDirectionCommandValidator : AbstractValidator<CreateDirectionCommand>
{
    public CreateDirectionCommandValidator()
    {
        AddContentRules();
        RuleFor(command => command.Status).IsInEnum();
        RuleFor(command => command.SortOrder).InclusiveBetween(0, Direction.SortOrderMax);
    }

    private void AddContentRules()
    {
        DirectionValidationRules.AddContentRules(
            this,
            command => command.Name,
            command => command.Code,
            command => command.Description,
            command => command.Icon);
    }
}

public sealed class UpdateDirectionCommandValidator : AbstractValidator<UpdateDirectionCommand>
{
    public UpdateDirectionCommandValidator()
    {
        DirectionValidationRules.AddContentRules(
            this,
            command => command.Name,
            command => command.Code,
            command => command.Description,
            command => command.Icon);
        RuleFor(command => command.Status).IsInEnum();
        RuleFor(command => command.SortOrder).InclusiveBetween(0, Direction.SortOrderMax);
        RuleFor(command => command.Version).GreaterThan(0u);
    }
}

public sealed class ChangeDirectionStatusCommandValidator : AbstractValidator<ChangeDirectionStatusCommand>
{
    public ChangeDirectionStatusCommandValidator()
    {
        RuleFor(command => command.Status).IsInEnum();
        RuleFor(command => command.Version).GreaterThan(0u);
    }
}

public sealed class ChangeDirectionSortOrderCommandValidator : AbstractValidator<ChangeDirectionSortOrderCommand>
{
    public ChangeDirectionSortOrderCommandValidator()
    {
        RuleFor(command => command.SortOrder).InclusiveBetween(0, Direction.SortOrderMax);
        RuleFor(command => command.Version).GreaterThan(0u);
    }
}

public sealed class ArchiveDirectionCommandValidator : AbstractValidator<ArchiveDirectionCommand>
{
    public ArchiveDirectionCommandValidator() => RuleFor(command => command.Version).GreaterThan(0u);
}

public sealed class RestoreDirectionCommandValidator : AbstractValidator<RestoreDirectionCommand>
{
    public RestoreDirectionCommandValidator() => RuleFor(command => command.Version).GreaterThan(0u);
}

internal static class DirectionValidationRules
{
    public static void AddContentRules<T>(
        AbstractValidator<T> validator,
        Expression<Func<T, string>> name,
        Expression<Func<T, string>> code,
        Expression<Func<T, string?>> description,
        Expression<Func<T, string?>> icon)
        where T : class
    {
        validator.RuleFor(name)
            .Must(BeValidName)
            .WithMessage(
                $"Name must contain 2-{Direction.NameMaxLength} Unicode scalar values after normalization.");
        validator.RuleFor(code)
            .Must(BeValidCode)
            .WithMessage($"Code must contain 2-{Direction.CodeMaxLength} letters or digits separated by spaces, underscores or hyphens.");
        validator.RuleFor(description)
            .Must(value => IsOptionalWithinLimit(value, Direction.DescriptionMaxLength))
            .WithMessage($"Description cannot exceed {Direction.DescriptionMaxLength} characters after trimming.");
        validator.RuleFor(icon)
            .Must(value => IsOptionalWithinLimit(value, Direction.IconMaxLength))
            .WithMessage($"Icon cannot exceed {Direction.IconMaxLength} characters after trimming.");
    }

    private static bool BeValidName(string? value)
    {
        try
        {
            _ = Direction.NormalizeName(value!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool BeValidCode(string? value)
    {
        try
        {
            _ = Direction.NormalizeCode(value!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsOptionalWithinLimit(string? value, int limit) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length <= limit;
}
