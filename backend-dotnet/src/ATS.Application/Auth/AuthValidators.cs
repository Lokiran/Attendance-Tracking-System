using Ats.Application.Validation;
using FluentValidation;

namespace Ats.Application.Auth;

// Field constraints of backend/app/schemas/auth.py. Property names are the JSON (snake_case) names.

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).PyEmail().OverridePropertyName("email");
        RuleFor(x => x.Password).RequiredString(1, 128).OverridePropertyName("password");
    }
}

public sealed class DevLoginRequestValidator : AbstractValidator<DevLoginRequest>
{
    public DevLoginRequestValidator() =>
        RuleFor(x => x.Email).RequiredString(1, 255).OverridePropertyName("email");
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).RequiredString(1, 128).OverridePropertyName("current_password");
        RuleFor(x => x.NewPassword).RequiredString(1, 128).OverridePropertyName("new_password");
    }
}
