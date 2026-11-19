using FluentValidation;
using TechStore.Application.DTOs.AdminAccounts;

namespace TechStore.Application.Validators.AdminAccounts;

public sealed class CreateAdminDtoValidator : AbstractValidator<CreateAdminDto>
{
    public CreateAdminDtoValidator()
    {
        RuleFor(dto => dto.FullName).NotEmpty().MaximumLength(200);
        RuleFor(dto => dto.Email).NotEmpty().EmailAddress().MaximumLength(256);
        // UserManager applies the configured Identity password policy. Do not
        // introduce a second, independently maintained password-strength policy.
        RuleFor(dto => dto.Password).NotEmpty().MinimumLength(8).MaximumLength(100);
    }
}
