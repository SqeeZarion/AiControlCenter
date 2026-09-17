using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Api;

//Ця модель приймає JSON під час створення агента
public sealed record CreateAgentDefinitionRequest(
    //До якого напрямку належить агент
    Guid? DirectionId,
    string? Name,
    string? Code,
    string? Description,
    AgentDefinitionStatus? Status,
    AgentExecutionType? ExecutionType)
{
    //Метод перетворює зовнішню HTTP-модель на внутрішню команду
    public CreateAgentDefinitionCommand ToCommand() => new(
        DirectionId!.Value, Name!, Code!, Description, Status!.Value, ExecutionType!.Value);
}

//модель, але для редагування агента
public sealed record UpdateAgentDefinitionRequest(
    Guid? DirectionId,
    string? Name,
    string? Code,
    string? Description,
    AgentDefinitionStatus? Status,
    AgentExecutionType? ExecutionType,
    uint? Version)
{
    public UpdateAgentDefinitionCommand ToCommand() => new(
        DirectionId!.Value, Name!, Code!, Description, Status!.Value, ExecutionType!.Value, Version!.Value);
}

public sealed class CreateAgentDefinitionRequestValidator : AbstractValidator<CreateAgentDefinitionRequest>
{
    public CreateAgentDefinitionRequestValidator()
    {
        RuleFor(request => request.DirectionId).NotNull().NotEqual(Guid.Empty);
        RuleFor(request => request.Name).NotNull();
        RuleFor(request => request.Code).NotNull();
        RuleFor(request => request.Status).NotNull();
        RuleFor(request => request.ExecutionType).NotNull();
    }
}

public sealed class UpdateAgentDefinitionRequestValidator : AbstractValidator<UpdateAgentDefinitionRequest>
{
    public UpdateAgentDefinitionRequestValidator()
    {
        RuleFor(request => request.DirectionId).NotNull().NotEqual(Guid.Empty);
        RuleFor(request => request.Name).NotNull();
        RuleFor(request => request.Code).NotNull();
        RuleFor(request => request.Status).NotNull();
        RuleFor(request => request.ExecutionType).NotNull();
        RuleFor(request => request.Version).NotNull();
    }
}
