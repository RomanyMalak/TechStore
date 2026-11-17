using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechStore.Application.DTOs.Products;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

// Every test instance owns a fresh GUID-named database. No shared fixture's
// catalogue is cleared, and the development database is never the test target.
public sealed class ProductSummarySqlServerTests : IAsyncLifetime
{
    private readonly SqlServerFixture database = new();

    public Task InitializeAsync() => database.InitializeAsync();
    public Task DisposeAsync() => database.DisposeAsync();

    [SqlServerFact]
    public async Task EmptyCatalogue_ReturnsZeroCounts_InOneServerQuery()
    {
        var commands = new ReadCounter();
        await using var context = database.Context(commands);
        var summary = await new ProductRepository(context).GetSummaryAsync();
        AssertCounts(summary, total: 0, active: 0, inStock: 0, outOfStock: 0);
        AssertSingleAggregate(commands);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task MixedCatalogue_ExcludesDeleted_IncludesInactiveStock_AndIgnoresCategoryActivationForActiveCount()
    {
        await using (var setup = database.Context())
        {
            var activeCategory = Category(active: true);
            var inactiveCategory = Category(active: false);
            setup.Categories.AddRange(activeCategory, inactiveCategory);
            await setup.SaveChangesAsync();
            var deletedInStock = ProductFor(activeCategory.Id, active: true, stock: 8);
            var deletedOutOfStock = ProductFor(activeCategory.Id, active: true, stock: 0);
            deletedInStock.SoftDelete();
            deletedOutOfStock.SoftDelete();
            setup.Products.AddRange(
                ProductFor(activeCategory.Id, active: true, stock: 5),
                ProductFor(inactiveCategory.Id, active: true, stock: 0),
                ProductFor(activeCategory.Id, active: false, stock: 7),
                ProductFor(activeCategory.Id, active: false, stock: 0),
                deletedInStock, deletedOutOfStock);
            await setup.SaveChangesAsync();
        }

        var commands = new ReadCounter();
        await using var context = database.Context(commands);
        var summary = await new ProductRepository(context).GetSummaryAsync();
        AssertCounts(summary, total: 4, active: 2, inStock: 2, outOfStock: 2);
        AssertSingleAggregate(commands);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task CatalogueLargerThanMaximumPage_ReturnsFullCountsWithoutPagingOrProductMaterialization()
    {
        await using (var setup = database.Context())
        {
            var category = Category(active: true);
            setup.Categories.Add(category);
            await setup.SaveChangesAsync();
            for (var index = 0; index < 205; index++)
                setup.Products.Add(ProductFor(category.Id, active: index % 2 == 0, stock: index % 5 == 0 ? 0 : 3));
            var removed = ProductFor(category.Id, active: true, stock: 10);
            removed.SoftDelete();
            setup.Products.Add(removed);
            await setup.SaveChangesAsync();
        }

        var commands = new ReadCounter();
        await using var context = database.Context(commands);
        var summary = await new ProductRepository(context).GetSummaryAsync();
        AssertCounts(summary, total: 205, active: 103, inStock: 164, outOfStock: 41);
        AssertSingleAggregate(commands);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task SummaryEndpoint_RequiresAdmin_AndReturnsOnlyCountsWithNoStore()
    {
        await using var factory = new SqlApiFactory(database);
        using var guest = factory.Client();
        var guestResponse = await guest.GetAsync("/api/products/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, guestResponse.StatusCode);
        var (customer, _) = await factory.CustomerAsync();
        using (customer)
        {
            var customerResponse = await customer.GetAsync("/api/products/summary");
            Assert.Equal(HttpStatusCode.Forbidden, customerResponse.StatusCode);
        }
        var (admin, _) = await factory.AdminAsync();
        using (admin)
        {
            var response = await admin.GetAsync("/api/products/summary");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var names = json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
            Assert.Equal(new[] { "activeProducts", "inStockProducts", "outOfStockProducts", "totalProducts" }, names);
            var summary = (await response.Content.ReadFromJsonAsync<ProductSummaryDto>())!;
            Assert.True(summary.TotalProducts > 0); // The isolated API's existing Development seeder ran.
            Assert.Equal(summary.TotalProducts, summary.InStockProducts + summary.OutOfStockProducts);
            using var swagger = JsonDocument.Parse(await admin.GetStringAsync("/swagger/v1/swagger.json"));
            var operation = swagger.RootElement.GetProperty("paths").GetProperty("/api/products/summary").GetProperty("get");
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        }
    }

    private static Category Category(bool active)
    {
        var category = new Category();
        category.UpdateDetails("Summary category " + Guid.NewGuid().ToString("N"), "Private SQL test category", null, active);
        return category;
    }
    private static Product ProductFor(int categoryId, bool active, int stock) =>
        Product.Create("Summary test product", "Private SQL test product", Guid.NewGuid().ToString("N"),
            "Test brand", 100m, null, stock, categoryId, active);
    private static void AssertCounts(ProductSummaryDto summary, int total, int active, int inStock, int outOfStock)
    {
        Assert.Equal(total, summary.TotalProducts);
        Assert.Equal(active, summary.ActiveProducts);
        Assert.Equal(inStock, summary.InStockProducts);
        Assert.Equal(outOfStock, summary.OutOfStockProducts);
    }
    private static void AssertSingleAggregate(ReadCounter counter)
    {
        var sql = Assert.Single(counter.Commands);
        Assert.Contains("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FETCH NEXT", sql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
