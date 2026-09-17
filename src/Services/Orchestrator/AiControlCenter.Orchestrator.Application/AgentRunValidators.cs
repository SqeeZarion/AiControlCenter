using AiControlCenter.Orchestrator.Domain;
using FluentValidation;

namespace AiControlCenter.Orchestrator.Application;

//Цей файл містить валідацію Application-рівня Orchestrator.

//команду створення запуску;
public sealed class CreateAgentRunCommandValidator : AbstractValidator<CreateAgentRunCommand>
{
    public CreateAgentRunCommandValidator()
    {
        RuleFor(command => command.AgentId).NotEmpty();
        RuleFor(command => command.OwnerUserId).NotEmpty();
        RuleFor(command => command.Input).MaximumLength(AgentRun.InputMaxLength);
        RuleFor(command => command.ExpectedOutcome).IsInEnum();
        RuleFor(command => command.CorrelationId).NotEmpty().MaximumLength(AgentRun.CorrelationIdMaxLength);
        RuleFor(command => command.DelegatedAuthorization)
            .Must(value => value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            .WithMessage("A delegated Bearer token is required.");
    }
}

//запит списку запусків.
public sealed class ListAgentRunsQueryValidator : AbstractValidator<ListAgentRunsQuery>
{
    public ListAgentRunsQueryValidator()
    {
        RuleFor(query => query.RequestUserId).NotEmpty();
        RuleFor(query => query.Status).Must(status => status is null || Enum.IsDefined(status.Value));
        RuleFor(query => query.AgentId).Must(id => id is null || id != Guid.Empty);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListAgentRunsQuery.MaximumPageSize);
    }
}
