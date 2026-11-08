using TechStore.Domain.Entities;

namespace TechStore.Application.Interfaces.Repositories;

public interface IAddressRepository
{
    Task<IReadOnlyList<Address>> GetAllAsync(string userId, bool tracked = false, CancellationToken cancellationToken = default);
    Task<Address?> GetByIdAsync(string userId, int id, CancellationToken cancellationToken = default);
    Task AddAsync(Address address, CancellationToken cancellationToken = default);
    void Delete(Address address);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
