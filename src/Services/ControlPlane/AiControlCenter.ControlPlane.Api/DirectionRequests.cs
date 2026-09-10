using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Api;

public sealed record UpdateDirectionRequest(
    string? Name,
    string? Code,
    string? Description,
    string? Icon,
    DirectionStatus? Status,
    int? SortOrder,
    uint? Version)
{
    public UpdateDirectionCommand ToCommand() => new(
        Name!,
        Code!,
        Description,
        Icon,
        Status!.Value,
        SortOrder!.Value,
        Version!.Value);
}

public sealed class UpdateDirectionRequestValidator : AbstractValidator<UpdateDirectionRequest>
{
    public UpdateDirectionRequestValidator()
    {
        RuleFor(request => request.Name).NotNull();
        RuleFor(request => request.Code).NotNull();
        RuleFor(request => request.Status).NotNull();
        RuleFor(request => request.SortOrder).NotNull();
        RuleFor(request => request.Version).NotNull();
    }
}
