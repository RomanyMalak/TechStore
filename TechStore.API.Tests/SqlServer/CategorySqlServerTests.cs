using Microsoft.EntityFrameworkCore;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Services;
using TechStore.Application.Validators.Categories;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class CategorySqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task ConcurrentReparenting_CannotCreateCycle_AndDeadlockBecomesConflict()
    {
        var first = new Category();
        first.UpdateDetails("SQL parent " + Guid.NewGuid().ToString("N"), null, null, true);
        var second = new Category();
        second.UpdateDetails("SQL parent " + Guid.NewGuid().ToString("N"), null, null, true);
        await using (var setup = database.Context())
        {
            setup.Categories.AddRange(first, second);
            await setup.SaveChangesAsync();
        }
        var gate = new CommerceSqlServerTests.ReadGate();
        async Task<bool> Reparent(Category category, int parent)
        {
            await using var context = database.Context();
            var service = new CategoryService(new GatedCategories(new CategoryRepository(context), gate),
                new CreateCategoryDtoValidator(), new UpdateCategoryDtoValidator());
            try
            {
                await service.UpdateCategoryAsync(category.Id, new UpdateCategoryDto { Name = category.Name, ParentCategoryId = parent, IsActive = true });
                return true;
            }
            catch (ConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Reparent(first, second.Id), Reparent(second, first.Id));
        Assert.Single(outcomes, success => success);
        await using var verification = database.Context();
        var rows = await verification.Categories.Where(category => category.Id == first.Id || category.Id == second.Id).ToListAsync();
        Assert.Single(rows, category => category.ParentCategoryId is null);
        Assert.Single(rows, category => category.ParentCategoryId is not null);
    }

    private sealed class GatedCategories(ICategoryRepository inner, CommerceSqlServerTests.ReadGate gate) : ICategoryRepository
    {
        public Task<Category?> GetByIdAsync(int id, bool inactive, CancellationToken token) => inner.GetByIdAsync(id, inactive, token);
        public Task<Category?> GetForUpdateAsync(int id, CancellationToken token) => inner.GetForUpdateAsync(id, token);
        public async Task<IReadOnlyList<Category>> GetAllAsync(bool inactive, CancellationToken token)
        {
            var categories = await inner.GetAllAsync(inactive, token);
            await gate.ArriveAsync(token);
            return categories;
        }
        public Task AddAsync(Category category, CancellationToken token) => inner.AddAsync(category, token);
        public void Update(Category category) => inner.Update(category);
        public void Deactivate(Category category) => inner.Deactivate(category);
        public Task<bool> ExistsByNameAsync(string name, int? excluding, CancellationToken token) => inner.ExistsByNameAsync(name, excluding, token);
        public Task<int> SaveChangesAsync(CancellationToken token) => inner.SaveChangesAsync(token);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) => inner.ExecuteInTransactionAsync(action, token);
    }
}
