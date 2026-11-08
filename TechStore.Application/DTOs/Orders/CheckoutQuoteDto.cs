using TechStore.Application.DTOs.Cart;
using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public sealed record PaymentOptionDto(PaymentMethod Value, string Label);
public sealed record CheckoutQuoteDto(CartResponseDto Cart, decimal ShippingCost, decimal TaxAmount,
    decimal TotalAmount, string ShippingLabel, IReadOnlyList<PaymentOptionDto> PaymentMethods, string Version);

