using TechStore.Application.Common;
using TechStore.Application.DTOs.Orders;
using TechStore.Domain.Entities;

namespace TechStore.Application.Interfaces.Repositories;

public interface IOrderRepository
{
    // A user ID scopes a Customer query; null is used only by the Admin use cases.
    Task<PagedResult<OrderListDto>> GetAllAsync(OrderQueryDto query, string? userId = null, CancellationToken cancellationToken = default);
    Task<Order?> GetByIdAsync(int id, string? userId = null, CancellationToken cancellationToken = default);
    Task<Order?> GetForUpdateAsync(int id, string? userId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Product>> GetProductsForUpdateAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
