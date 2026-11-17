using Microsoft.EntityFrameworkCore;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Exceptions;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Persistence;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests;

public class CatalogRepositoryTests
{
    [Fact]
    public async Task CustomerReads_HideInactiveDeletedAndInactiveCategoryProducts()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var page = await database.Products.GetAllAsync(new ProductQueryDto());
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, product => Assert.Contains(product.Id, new[] { 1, 2, 3 }));
        Assert.Null(await database.Products.GetByIdAsync(4));
        Assert.Null(await database.Products.GetByIdAsync(5));
        Assert.Null(await database.Products.GetByIdAsync(6));
    }

    [Fact]
    public async Task AdminReads_IncludeInactiveProductsButStillHideDeletedRows()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var page = await database.Products.GetAllAsync(new ProductQueryDto(), includeInactive: true);
        Assert.Equal(5, page.TotalCount);
        Assert.NotNull(await database.Products.GetByIdAsync(4, includeInactive: true));
        Assert.NotNull(await database.Products.GetByIdAsync(5, includeInactive: true));
        Assert.Null(await database.Products.GetByIdAsync(6, includeInactive: true));
    }

    [Fact]
    public async Task ProductQueries_CombineFiltersAndSortPageByEffectivePrice()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var filtered = await database.Products.GetAllAsync(new ProductQueryDto
        {
            Search = "Laptop", Brand = "Alpha", CategoryId = 1, MinPrice = 80, MaxPrice = 110, InStockOnly = true
        });
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(1, Assert.Single(filtered.Items).Id);
        var page = await database.Products.GetAllAsync(new ProductQueryDto
        {
            SortBy = ProductSort.PriceAscending, PageNumber = 2, PageSize = 1
        });
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task ReadQueries_AreNotTrackedAndDetailsLoadAllImages()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var page = await database.Products.GetAllAsync(new ProductQueryDto { Search = "Laptop A" });
        Assert.True(Assert.Single(Assert.Single(page.Items).Images).IsPrimary);
        var details = await database.Products.GetByIdAsync(1);
        Assert.Equal(3, details!.Images.Count);
        Assert.Equal("Active category", details.Category.Name);
        Assert.Empty(database.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task WriteQueries_TrackInactiveProductsAndExcludeDeletedRows()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var product = await database.Products.GetForUpdateAsync(4);
        Assert.NotNull(product);
        Assert.Equal(EntityState.Unchanged, database.Context.Entry(product).State);
        Assert.Null(await database.Products.GetForUpdateAsync(6));
    }

    [Fact]
    public async Task SkuLookup_ReservesDeletedSkusAndExcludesTheCurrentProduct()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        Assert.True(await database.Products.ExistsBySkuAsync("SKU-6"));
        Assert.True(await database.Products.ExistsBySkuAsync("SKU-1"));
        Assert.False(await database.Products.ExistsBySkuAsync("SKU-1", excludingId: 1));
    }

    [Fact]
    public async Task CategoryReads_HonorVisibilityAndDoNotTrackEntities()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var categories = new CategoryRepository(database.Context);
        Assert.Single(await categories.GetAllAsync());
        Assert.Equal(2, (await categories.GetAllAsync(includeInactive: true)).Count);
        Assert.Null(await categories.GetByIdAsync(2));
        Assert.NotNull(await categories.GetByIdAsync(2, includeInactive: true));
        Assert.Empty(database.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task StaleOriginalVersion_ProducesConflictAndPreservesTheOtherWrite()
    {
        await using var database = await CatalogDatabase.CreateAsync();
        var product = (await database.Products.GetForUpdateAsync(1))!;
        var originalVersion = product.RowVersion.ToArray();
        await using (var otherContext = new ApplicationDbContext(database.Options))
        {
            var newer = await otherContext.Products.SingleAsync(entity => entity.Id == 1);
            newer.AdjustStock(1);
            // InMemory has no SQL Server rowversion generator; simulate the database's new value.
            newer.RowVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 2 };
            await otherContext.SaveChangesAsync();
        }
        product.AdjustStock(2);
        database.Products.Update(product, originalVersion);
        var error = await Assert.ThrowsAsync<ConflictException>(() => database.Products.SaveChangesAsync());
        Assert.IsType<DbUpdateConcurrencyException>(error.InnerException);
        await using var verification = new ApplicationDbContext(database.Options);
        Assert.Equal(6, (await verification.Products.SingleAsync(entity => entity.Id == 1)).Stock);
    }

    private sealed class CatalogDatabase : IAsyncDisposable
    {
        public DbContextOptions<ApplicationDbContext> Options { get; }
        public ApplicationDbContext Context { get; }
        public ProductRepository Products { get; }

        private CatalogDatabase()
        {
            Options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
            Context = new ApplicationDbContext(Options);
            Products = new ProductRepository(Context);
        }

        public static async Task<CatalogDatabase> CreateAsync()
        {
            var database = new CatalogDatabase();
            database.Context.Categories.AddRange(new Category { Id = 1, Name = "Active category" },
                new Category { Id = 2, Name = "Inactive category", IsActive = false });
            var products = new[]
            {
                Product.Create("Laptop A", "Description", "SKU-1", "Alpha", 100, 90, 5, 1),
                Product.Create("Laptop B", "Description", "SKU-2", "Beta", 50, null, 0, 1),
                Product.Create("Laptop C", "Description", "SKU-3", "Alpha", 200, null, 5, 1),
                Product.Create("Inactive", "Description", "SKU-4", "Alpha", 10, null, 5, 1, false),
                Product.Create("Inactive category product", "Description", "SKU-5", "Alpha", 20, null, 5, 2),
                Product.Create("Deleted", "Description", "SKU-6", "Alpha", 30, null, 5, 1)
            };
            for (var index = 0; index < products.Length; index++)
            {
                products[index].Id = index + 1;
                products[index].RowVersion = ApiFactory.Version;
            }
            products[^1].SoftDelete();
            products[0].Images.Add(new ProductImage { ImageUrl = "https://example.com/primary.png", IsPrimary = true });
            products[0].Images.Add(new ProductImage { ImageUrl = "https://example.com/secondary.png", SortOrder = 1 });
            products[0].Images.Add(new ProductImage { ImageUrl = "https://example.com/third.png", SortOrder = 2 });
            database.Context.Products.AddRange(products);
            await database.Context.SaveChangesAsync();
            database.Context.ChangeTracker.Clear();
            return database;
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
