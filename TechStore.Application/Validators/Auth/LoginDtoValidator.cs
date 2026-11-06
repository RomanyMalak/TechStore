using FluentValidation;
using TechStore.Application.DTOs.Auth;

namespace TechStore.Application.Validators.Auth;

public sealed class LoginDtoValidator : AbstractValidator<LoginDto>
{
    public LoginDtoValidator()
    {
        RuleFor(dto => dto.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(dto => dto.Password).NotEmpty().MaximumLength(100);
    }
}
