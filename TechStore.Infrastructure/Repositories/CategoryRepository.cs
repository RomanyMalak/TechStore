using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Persistence;

namespace TechStore.Infrastructure.Repositories;

public sealed class CategoryRepository : ICategoryRepository
{
    private readonly ApplicationDbContext _context;

    public CategoryRepository(ApplicationDbContext context) => _context = context;

    public Task<Category?> GetByIdAsync(int id, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        _context.Categories.AsNoTracking().Include(category => category.ParentCategory)
            .SingleOrDefaultAsync(category => category.Id == id && (includeInactive || category.IsActive), cancellationToken);

    public Task<Category?> GetForUpdateAsync(int id, CancellationToken cancellationToken = default) =>
        _context.Categories.Include(category => category.ParentCategory)
            .SingleOrDefaultAsync(category => category.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Category>> GetAllAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await _context.Categories.AsNoTracking().Include(category => category.ParentCategory)
            .Where(category => includeInactive || category.IsActive)
            .OrderBy(category => category.Name).ThenBy(category => category.Id).ToListAsync(cancellationToken);

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default) =>
        await _context.Categories.AddAsync(category, cancellationToken);

    public void Update(Category category) => _context.Entry(category).State = EntityState.Modified;

    public void Deactivate(Category category) => Update(category);

    public Task<bool> ExistsByNameAsync(string name, int? excludingId = null, CancellationToken cancellationToken = default) =>
        _context.Categories.AsNoTracking().AnyAsync(category => category.Name == name
            && (!excludingId.HasValue || category.Id != excludingId.Value), cancellationToken);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _context.SaveChangesAsync(cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var result = await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            throw new ConflictException("The category hierarchy changed concurrently. Retry the operation.", exception);
        }
    }
}
