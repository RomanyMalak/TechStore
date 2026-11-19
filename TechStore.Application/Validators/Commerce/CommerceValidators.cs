using FluentValidation;
using TechStore.Application.DTOs.Addresses;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Domain.Enums;

namespace TechStore.Application.Validators.Commerce;

public sealed class AddToCartValidator : AbstractValidator<AddToCartDto>
{
    public AddToCartValidator()
    {
        RuleFor(dto => dto.ProductId).GreaterThan(0);
        RuleFor(dto => dto.Quantity).GreaterThan(0);
        RuleFor(dto => dto.ClientRequestId).NotEqual(Guid.Empty).When(dto => dto.ClientRequestId.HasValue);
    }
}
public sealed class UpdateCartItemValidator : AbstractValidator<UpdateCartItemDto>
{
    public UpdateCartItemValidator() { RuleFor(dto => dto.Quantity).GreaterThan(0); }
}
public sealed class ShippingAddressValidator : AbstractValidator<CreateAddressDto>
{
    public const string PhonePattern = @"^\+?[0-9][0-9 ()\-]{6,28}[0-9]$";

    public ShippingAddressValidator()
    {
        RuleFor(dto => dto.FullName).NotEmpty().MaximumLength(200);
        RuleFor(dto => dto.Phone).NotEmpty().MaximumLength(30).Matches(PhonePattern);
        RuleFor(dto => dto.City).NotEmpty().MaximumLength(100);
        RuleFor(dto => dto.Street).NotEmpty().MaximumLength(300);
        RuleFor(dto => dto.Building).NotEmpty().MaximumLength(100);
    }
}
public sealed class CreateOrderValidator : AbstractValidator<CreateOrderDto>
{
    public CreateOrderValidator()
    {
        RuleFor(dto => dto.ClientRequestId).NotEmpty();
        RuleFor(dto => dto.QuoteVersion).NotEmpty().Matches("^[A-Fa-f0-9]{64}$");
        RuleFor(dto => dto.PaymentMethod).Equal(PaymentMethod.CashOnDelivery).WithMessage("Only cash on delivery is supported.");
        RuleFor(dto => dto).Must(dto => dto.ShippingAddressId is > 0 && dto.ShippingAddress is null
            || dto.ShippingAddressId is null && dto.ShippingAddress is not null)
            .WithMessage("Provide either your saved address ID or a shipping address.");
        When(dto => dto.ShippingAddress is not null, () => RuleFor(dto => dto.ShippingAddress!).SetValidator(new ShippingAddressValidator()));
    }
}
