using FluentValidation;
using TechStore.Application.DTOs.Auth;

namespace TechStore.Application.Validators.Auth;

public sealed class RegisterDtoValidator : AbstractValidator<RegisterDto>
{
    public RegisterDtoValidator()
    {
        RuleFor(dto => dto.FullName).NotEmpty().MaximumLength(200);
        RuleFor(dto => dto.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(dto => dto.PhoneNumber).MaximumLength(30);
        RuleFor(dto => dto.Password).NotEmpty().MinimumLength(8).MaximumLength(100);
        RuleFor(dto => dto.ConfirmPassword).NotEmpty().Equal(dto => dto.Password)
            .WithMessage("Password confirmation does not match.");
    }
}
