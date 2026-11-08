using System.Text.Json;
using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Mapping;
using TechStore.Domain.Entities;

namespace TechStore.Application.Services;

public sealed class CheckoutService(ICommerceRepository repository, IValidator<CreateOrderDto> validator, CheckoutPolicy policy) : ICheckoutService
{
    public async Task<CheckoutQuoteDto> GetQuoteAsync(string userId, CancellationToken cancellationToken = default) =>
        (await repository.GetCartAsync(CartOwner.User(userId), false, cancellationToken) ?? new Cart { UserId = userId }).ToQuote(policy);

    public async Task<OrderResponseDto> GetOrderAsync(string userId, int id, CancellationToken cancellationToken = default) =>
        (await repository.GetOrderAsync(userId, id, cancellationToken) ?? throw new NotFoundException(nameof(Order), id)).ToOrderResponse();

    public async Task<OrderResponseDto> CreateOrderAsync(string userId, CreateOrderDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(validator, dto, cancellationToken);
        var fingerprint = CommerceMapping.Hash(JsonSerializer.Serialize(dto));
        return await repository.ExecuteInTransactionAsync(async token =>
        {
            var previous = await repository.GetOrderByRequestAsync(userId, dto.ClientRequestId, token);
            if (previous is not null)
            {
                if (previous.RequestFingerprint != fingerprint) throw new CommerceConflictException("IdempotencyMismatch", "This request ID was already used for different order details.");
                return previous.ToOrderResponse();
            }
            var cart = await repository.GetCartAsync(CartOwner.User(userId), true, token);
            if (cart is null || cart.Items.Count == 0) throw new CommerceConflictException("EmptyCart", "The cart is empty.");
            if (cart.Items.Any(item => !item.Product.IsPurchasable())) throw new CommerceConflictException("ProductUnavailable", "A product is no longer available.");
            if (cart.Items.Any(item => item.Quantity > item.Product.Stock)) throw new CommerceConflictException("StockUnavailable", "Stock changed. Review your cart.");
            if (cart.ToQuote(policy).Version != dto.QuoteVersion.ToUpperInvariant()) throw new CommerceConflictException("CartChanged", "Prices or cart contents changed. Review the new total.");
            var address = dto.ShippingAddress;
            if (dto.ShippingAddressId is int addressId)
            {
                var saved = await repository.GetAddressAsync(userId, addressId, token) ?? throw new NotFoundException(nameof(Address), addressId);
                address = new() { FullName = saved.FullName, Phone = saved.Phone, City = saved.City, Street = saved.Street, Building = saved.Building };
            }
            var order = RequestValidation.ApplyDomainRules(() => Order.Place(userId, dto.ClientRequestId, fingerprint,
                address!.FullName.Trim(), address.Phone.Trim(), $"{address.City.Trim()}، {address.Street.Trim()}، {address.Building.Trim()}",
                policy.ShippingCost, cart.Items.Select(item => (item.Product, item.Quantity))));
            foreach (var line in cart.Items) RequestValidation.ApplyDomainRules(() => { line.Product.AdjustStock(-line.Quantity); return line.Product; });
            await repository.AddOrderAsync(order, token);
            cart.Clear();
            await repository.SaveChangesAsync(token);
            return order.ToOrderResponse();
        }, cancellationToken);
    }

    public async Task<OrderResponseDto> GetOrderByRequestAsync(string userId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new UnauthorizedException();
        if (requestId == Guid.Empty) throw new Exceptions.ValidationException(nameof(requestId), "A valid request identifier is required.");
        return (await repository.GetOrderByRequestAsync(userId, requestId, cancellationToken)
            ?? throw new NotFoundException(nameof(Order))).ToOrderResponse();
    }
}
