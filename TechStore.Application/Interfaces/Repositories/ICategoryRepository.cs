using TechStore.Domain.Entities;

namespace TechStore.Application.Interfaces.Repositories;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(int id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<Category?> GetForUpdateAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Category>> GetAllAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
    Task AddAsync(Category category, CancellationToken cancellationToken = default);
    void Update(Category category);
    void Deactivate(Category category);
    Task<bool> ExistsByNameAsync(string name, int? excludingId = null, CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    // Infrastructure supplies a serializable transaction; Application supplies the use case.
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
