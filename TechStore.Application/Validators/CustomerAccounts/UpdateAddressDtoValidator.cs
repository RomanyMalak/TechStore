using FluentValidation;
using TechStore.Application.DTOs.Addresses;
using TechStore.Application.Validators.Commerce;

namespace TechStore.Application.Validators.CustomerAccounts;

public sealed class UpdateAddressDtoValidator : AbstractValidator<UpdateAddressDto>
{
    public UpdateAddressDtoValidator() => Include(new ShippingAddressValidator());
}
