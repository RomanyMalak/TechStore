using TechStore.Application.Common;
using TechStore.Domain.Entities;

namespace TechStore.Application.Interfaces.Repositories;

public interface ICommerceRepository
{
    Task<Cart?> GetCartAsync(CartOwner owner, bool tracked, CancellationToken cancellationToken);
    Task<Product?> GetProductAsync(int id, CancellationToken cancellationToken);
    Task<Address?> GetAddressAsync(string userId, int id, CancellationToken cancellationToken);
    Task<Order?> GetOrderAsync(string userId, int id, CancellationToken cancellationToken);
    Task<Order?> GetOrderByRequestAsync(string userId, Guid requestId, CancellationToken cancellationToken);
    Task AddCartAsync(Cart cart, CancellationToken cancellationToken);
    void RemoveCart(Cart cart);
    Task AddOrderAsync(Order order, CancellationToken cancellationToken);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
