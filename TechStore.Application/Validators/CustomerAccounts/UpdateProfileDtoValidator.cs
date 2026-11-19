using FluentValidation;
using TechStore.Application.DTOs.CustomerAccounts;
using TechStore.Application.Validators.Commerce;

namespace TechStore.Application.Validators.CustomerAccounts;

public sealed class UpdateProfileDtoValidator : AbstractValidator<UpdateProfileDto>
{
    public UpdateProfileDtoValidator()
    {
        RuleFor(dto => dto.FullName).NotEmpty().MaximumLength(200);
        RuleFor(dto => dto.PhoneNumber).MaximumLength(30).Matches(ShippingAddressValidator.PhonePattern)
            .When(dto => dto.PhoneNumber is not null);
    }
}
