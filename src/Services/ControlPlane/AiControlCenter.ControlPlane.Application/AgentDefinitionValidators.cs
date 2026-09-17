using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Application;

public sealed class ListAgentDefinitionsQueryValidator : AbstractValidator<ListAgentDefinitionsQuery>
{
    public ListAgentDefinitionsQueryValidator()
    {
        RuleFor(query => query.Status).Must(status => status is null || Enum.IsDefined(status.Value));
        RuleFor(query => query.DirectionId).Must(id => id is null || id != Guid.Empty);
        RuleFor(query => query.Search).MaximumLength(AgentDefinition.NameMaxLength);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListAgentDefinitionsQuery.MaximumPageSize);
    }
}

public sealed class CreateAgentDefinitionCommandValidator : AbstractValidator<CreateAgentDefinitionCommand>
{
    public CreateAgentDefinitionCommandValidator()
    {
        RuleFor(command => command.DirectionId).NotEmpty();
        AgentDefinitionValidationRules.AddContentRules(this);
    }
}

public sealed class UpdateAgentDefinitionCommandValidator : AbstractValidator<UpdateAgentDefinitionCommand>
{
    public UpdateAgentDefinitionCommandValidator()
    {
        RuleFor(command => command.DirectionId).NotEmpty();
        RuleFor(command => command.Version).GreaterThan(0u);
        AgentDefinitionValidationRules.AddContentRules(this);
    }
}

public sealed class ChangeAgentDefinitionStatusCommandValidator
    : AbstractValidator<ChangeAgentDefinitionStatusCommand>
{
    public ChangeAgentDefinitionStatusCommandValidator()
    {
        RuleFor(command => command.Status).IsInEnum();
        RuleFor(command => command.Version).GreaterThan(0u);
    }
}

public sealed class ArchiveAgentDefinitionCommandValidator : AbstractValidator<ArchiveAgentDefinitionCommand>
{
    public ArchiveAgentDefinitionCommandValidator() => RuleFor(command => command.Version).GreaterThan(0u);
}

public sealed class RestoreAgentDefinitionCommandValidator : AbstractValidator<RestoreAgentDefinitionCommand>
{
    public RestoreAgentDefinitionCommandValidator() => RuleFor(command => command.Version).GreaterThan(0u);
}

internal static class AgentDefinitionValidationRules
{
    public static void AddContentRules<T>(AbstractValidator<T> validator)
        where T : class
    {
        validator.RuleFor(command => GetName(command)).Must(BeValidName)
            .WithName("Name")
            .WithMessage($"Name must contain 2-{AgentDefinition.NameMaxLength} Unicode scalar values after normalization.");
        validator.RuleFor(command => GetCode(command)).Must(BeValidCode)
            .WithName("Code")
            .WithMessage($"Code must contain 2-{AgentDefinition.CodeMaxLength} letters or digits separated by spaces, underscores or hyphens.");
        validator.RuleFor(command => GetDescription(command)).Must(BeValidDescription)
            .WithName("Description")
            .WithMessage($"Description cannot exceed {AgentDefinition.DescriptionMaxLength} characters after trimming.");
        validator.RuleFor(command => GetStatus(command)).IsInEnum().WithName("Status");
        validator.RuleFor(command => GetExecutionType(command))
            .Equal(AgentExecutionType.Test)
            .WithName("ExecutionType")
            .WithMessage("Only the Test execution type is supported.");
    }

    private static string GetName<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Name,
        UpdateAgentDefinitionCommand update => update.Name,
        _ => string.Empty,
    };

    private static string GetCode<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Code,
        UpdateAgentDefinitionCommand update => update.Code,
        _ => string.Empty,
    };

    private static string? GetDescription<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Description,
        UpdateAgentDefinitionCommand update => update.Description,
        _ => null,
    };

    private static AgentDefinitionStatus GetStatus<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Status,
        UpdateAgentDefinitionCommand update => update.Status,
        _ => (AgentDefinitionStatus)(-1),
    };

    private static AgentExecutionType GetExecutionType<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.ExecutionType,
        UpdateAgentDefinitionCommand update => update.ExecutionType,
        _ => (AgentExecutionType)(-1),
    };

    private static bool BeValidName(string? value)
    {
        try { _ = AgentDefinition.NormalizeName(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool BeValidCode(string? value)
    {
        try { _ = AgentDefinition.NormalizeCode(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool BeValidDescription(string? value)
    {
        try { _ = AgentDefinition.NormalizeDescription(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
