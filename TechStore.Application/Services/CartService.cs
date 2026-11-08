using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Mapping;
using TechStore.Domain.Entities;

namespace TechStore.Application.Services;

public sealed class CartService(ICommerceRepository repository, IValidator<AddToCartDto> addValidator,
    IValidator<UpdateCartItemDto> updateValidator) : ICartService
{
    public async Task<CartResponseDto> GetAsync(CartOwner owner, CancellationToken cancellationToken = default) =>
        (await repository.GetCartAsync(owner, false, cancellationToken) ?? EmptyCart(owner)).ToCartResponse();

    public async Task<CartResponseDto> AddAsync(CartOwner owner, AddToCartDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(addValidator, dto, cancellationToken);
        return await ChangeAsync(owner, dto.ProductId, dto.Quantity, true, cancellationToken);
    }
    public async Task<CartResponseDto> UpdateAsync(CartOwner owner, int productId, UpdateCartItemDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(updateValidator, dto, cancellationToken);
        return await ChangeAsync(owner, productId, dto.Quantity, false, cancellationToken);
    }
    private Task<CartResponseDto> ChangeAsync(CartOwner owner, int productId, int quantity, bool add, CancellationToken cancellationToken) =>
        repository.ExecuteInTransactionAsync(async token =>
        {
            var cart = await repository.GetCartAsync(owner, true, token);
            if (!add && cart?.Items.Any(item => item.ProductId == productId) != true)
                throw new NotFoundException(nameof(CartItem), productId);
            var product = await repository.GetProductAsync(productId, token) ?? throw new NotFoundException(nameof(Product), productId);
            if (!product.IsPurchasable()) throw new CommerceConflictException("ProductUnavailable", "The product is no longer available.");
            var total = (long)quantity + (add ? cart?.Items.SingleOrDefault(item => item.ProductId == productId)?.Quantity ?? 0 : 0);
            if (total > product.Stock) throw new CommerceConflictException("StockUnavailable", "The requested quantity exceeds available stock.");
            var isNew = cart is null;
            cart ??= EmptyCart(owner);
            RequestValidation.ApplyDomainRules(() => { cart.SetQuantity(productId, (int)total); return cart; });
            cart.Items.Single(item => item.ProductId == productId).Product = product;
            if (isNew) await repository.AddCartAsync(cart, token);
            await repository.SaveChangesAsync(token);
            return cart.ToCartResponse();
        }, cancellationToken);

    public Task<CartResponseDto> RemoveAsync(CartOwner owner, int productId, CancellationToken cancellationToken = default) =>
        repository.ExecuteInTransactionAsync(async token =>
        {
            var cart = await repository.GetCartAsync(owner, true, token);
            if (cart is null) return EmptyCart(owner).ToCartResponse();
            cart.Remove(productId);
            await repository.SaveChangesAsync(token);
            return cart.ToCartResponse();
        }, cancellationToken);

    public Task<CartResponseDto> MergeAsync(string userId, string? guestKey, CancellationToken cancellationToken = default) =>
        repository.ExecuteInTransactionAsync(async token =>
        {
            var owner = CartOwner.User(userId);
            var cart = await repository.GetCartAsync(owner, true, token);
            var guest = guestKey is null ? null : await repository.GetCartAsync(CartOwner.Guest(guestKey), true, token);
            if (guest is null || guest.Items.Count == 0) return (cart ?? EmptyCart(owner)).ToCartResponse();
            var isNew = cart is null;
            cart ??= EmptyCart(owner);
            foreach (var line in guest.Items)
            {
                var quantity = (long)line.Quantity + (cart.Items.SingleOrDefault(item => item.ProductId == line.ProductId)?.Quantity ?? 0);
                if (quantity > int.MaxValue) throw new CommerceConflictException("StockUnavailable", "The merged quantity is too large.");
                RequestValidation.ApplyDomainRules(() => { cart.SetQuantity(line.ProductId, (int)quantity); return cart; });
                cart.Items.Single(item => item.ProductId == line.ProductId).Product = line.Product;
            }
            // Preserve requested quantities even if stock changed; the cart flags invalid lines for correction.
            repository.RemoveCart(guest);
            if (isNew) await repository.AddCartAsync(cart, token);
            await repository.SaveChangesAsync(token);
            return cart.ToCartResponse();
        }, cancellationToken);

    private static Cart EmptyCart(CartOwner owner) => new() { UserId = owner.UserId, GuestKey = owner.GuestKey };
}
