using FluentValidation;

//Валідатори перевіряють форму вхідних даних до запуску бізнес-логіки.
namespace AiControlCenter.Identity.Application;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(request => request.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(request => request.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(request => request.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(request => request.DisplayName).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(request => request.TemporaryPassword).MinimumLength(12).MaximumLength(128);
        RuleFor(request => request.Roles).NotEmpty();
        RuleForEach(request => request.Roles).Must(BeSystemRole).WithMessage("Unknown system role.");
    }

    private static bool BeSystemRole(string role) => role is "Admin" or "Developer" or "User";
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(request => request.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(request => request.NewPassword).MinimumLength(12).MaximumLength(128)
            .NotEqual(request => request.CurrentPassword);
    }
}

public sealed class ResetUserPasswordRequestValidator : AbstractValidator<ResetUserPasswordRequest>
{
    public ResetUserPasswordRequestValidator() =>
        RuleFor(request => request.TemporaryPassword).MinimumLength(12).MaximumLength(128);
}

public sealed class ReplaceUserRolesRequestValidator : AbstractValidator<ReplaceUserRolesRequest>
{
    public ReplaceUserRolesRequestValidator()
    {
        RuleFor(request => request.Roles).NotEmpty();
        RuleForEach(request => request.Roles)
            .Must(role => role is "Admin" or "Developer" or "User")
            .WithMessage("Unknown system role.");
    }
}
