using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using FluentValidation;

namespace AiControlCenter.Orchestrator.Api;

//Цей файл описує HTTP-запит на створення нового запуску агента.

//Це модель JSON, який Orchestrator приймає від frontend.
public sealed record CreateAgentRunRequest(Guid? AgentId, string? Input, TestRunOutcome? ExpectedOutcome)
{
    public CreateAgentRunCommand ToCommand(
        Guid ownerUserId,
        string correlationId,
        string delegatedAuthorization) =>
        new(AgentId!.Value, Input ?? string.Empty, ExpectedOutcome!.Value,
            ownerUserId, correlationId, delegatedAuthorization);
}

public sealed class CreateAgentRunRequestValidator : AbstractValidator<CreateAgentRunRequest>
{
    public CreateAgentRunRequestValidator()
    {
        RuleFor(request => request.AgentId).NotNull().NotEqual(Guid.Empty);
        RuleFor(request => request.Input).NotNull();
        RuleFor(request => request.ExpectedOutcome).NotNull();
    }
}
