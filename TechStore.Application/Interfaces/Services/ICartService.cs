using TechStore.Application.Common;
using TechStore.Application.DTOs.Cart;

namespace TechStore.Application.Interfaces.Services;

public interface ICartService
{
    Task<CartResponseDto> GetAsync(CartOwner owner, CancellationToken cancellationToken = default);
    Task<CartResponseDto> AddAsync(CartOwner owner, AddToCartDto dto, CancellationToken cancellationToken = default);
    Task<CartResponseDto> UpdateAsync(CartOwner owner, int productId, UpdateCartItemDto dto, CancellationToken cancellationToken = default);
    Task<CartResponseDto> RemoveAsync(CartOwner owner, int productId, CancellationToken cancellationToken = default);
    Task<CartResponseDto> MergeAsync(string userId, string? guestKey, CancellationToken cancellationToken = default);
}
