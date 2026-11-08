using TechStore.Application.DTOs.Orders;

namespace TechStore.Application.Interfaces.Services;

public interface ICheckoutService
{
    Task<CheckoutQuoteDto> GetQuoteAsync(string userId, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> CreateOrderAsync(string userId, CreateOrderDto dto, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> GetOrderAsync(string userId, int id, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> GetOrderByRequestAsync(string userId, Guid requestId, CancellationToken cancellationToken = default);
}
