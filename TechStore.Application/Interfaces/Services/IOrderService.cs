using TechStore.Application.Common;
using TechStore.Application.DTOs.Orders;

namespace TechStore.Application.Interfaces.Services;

public interface IOrderService
{
    Task<PagedResult<OrderListDto>> GetCustomerOrdersAsync(string userId, OrderQueryDto query, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> GetCustomerOrderAsync(string userId, int id, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> CancelCustomerOrderAsync(string userId, int id, CancelOrderDto dto, CancellationToken cancellationToken = default);
    Task<PagedResult<OrderListDto>> GetAdminOrdersAsync(OrderQueryDto query, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> GetAdminOrderAsync(int id, CancellationToken cancellationToken = default);
    Task<OrderResponseDto> UpdateStatusAsync(string adminId, int id, UpdateOrderStatusDto dto, CancellationToken cancellationToken = default);
}
