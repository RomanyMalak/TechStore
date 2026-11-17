using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechStore.Application.DTOs.Analytics;
using TechStore.Application.Exceptions;
using TechStore.Application.Services;
using TechStore.Application.Validators.Analytics;
using TechStore.Domain.Entities;
using TechStore.Domain.Enums;
using TechStore.Infrastructure.Identity;
using TechStore.Infrastructure.Persistence;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

// Each fact owns a fresh GUID database; no development tables are read or mutated.
public sealed class AnalyticsSqlServerTests : IAsyncLifetime
{
    private readonly SqlServerFixture database = new();
    private readonly DateTime now = new(2026, 11, 9, 18, 30, 0, DateTimeKind.Utc);
    public Task InitializeAsync() => database.InitializeAsync();
    public Task DisposeAsync() => database.DisposeAsync();

    [SqlServerFact]
    public async Task EmptyDatabase_ReturnsKnownZeroValues_WithoutMaterializingEntities()
    {
        var counter = new Reads();
        await using var context = database.Context(counter);
        var result = await Service(context).GetAsync(new());
        Assert.Equal(0, result.Overview.TotalOrders);
        Assert.Equal(0, result.Overview.ConfirmedOrders);
        Assert.Equal(0m, result.Overview.SalesAmount);
        Assert.Equal(0, result.Overview.UnitsSold);
        Assert.Equal(0m, result.Overview.AverageOrderValue);
        Assert.Empty(result.MostAdded);
        Assert.Empty(result.MostOrdered);
        Assert.Empty(result.Performance.Items);
        Assert.Equal(0, result.Performance.TotalCount);
        Assert.Equal(0, result.Performance.TotalPages);
        Assert.Equal(DateTimeKind.Utc, result.CartTrackingStartedAtUtc.Kind);
        Assert.Equal(7, counter.Commands.Count);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task StatusesSnapshotPricesAndHalfOpenBoundaries_AreCalculatedInSql_DespiteProductRetirement()
    {
        int firstId, secondId;
        await using (var setup = database.Context())
        {
            var (user, products) = await SeedProducts(setup, 3);
            var first = products[0]; var second = products[1]; var excluded = products[2];
            firstId = first.Id; secondId = second.Id;
            var start = now.AddDays(-30);
            setup.Orders.AddRange(
                Order(user.Id, first, 2, 10m, OrderStatus.Confirmed, now.AddDays(-1)),
                Order(user.Id, first, 3, 10m, OrderStatus.Processing, now.AddDays(-2)),
                Order(user.Id, second, 4, 20m, OrderStatus.Shipped, now.AddDays(-3)),
                Order(user.Id, second, 5, 20m, OrderStatus.Delivered, now.AddDays(-4)),
                Order(user.Id, first, 1, 10m, OrderStatus.Confirmed, start),
                Order(user.Id, excluded, 100, 99m, OrderStatus.Pending, now.AddDays(-1)),
                Order(user.Id, excluded, 200, 99m, OrderStatus.Cancelled, now.AddDays(-1)),
                Order(user.Id, excluded, 300, 99m, OrderStatus.Returned, now.AddDays(-1)),
                Order(user.Id, first, 900, 10m, OrderStatus.Confirmed, start.AddTicks(-1)),
                Order(user.Id, first, 900, 10m, OrderStatus.Confirmed, now),
                Order(user.Id, first, 900, 10m, OrderStatus.Confirmed, now.AddDays(1)));
            setup.Set<CartAdditionEvent>().AddRange(Event(first, 2, start), Event(first, 3, now.AddDays(-1)),
                Event(second, 7, now.AddDays(-1)), Event(first, 999, start.AddTicks(-1)),
                Event(excluded, 999, now), Event(excluded, 999, now.AddDays(1)));
            first.SoftDelete();
            second.UpdateDetails(second.Name, second.Description, second.SKU, second.Brand, 900m, 800m, 1000, second.CategoryId, false);
            await setup.SaveChangesAsync();
        }
        var counter = new Reads();
        await using var context = database.Context(counter);
        var result = await Service(context).GetAsync(new() { PageSize = 1 });
        Assert.Equal(8, result.Overview.TotalOrders);
        Assert.Equal(5, result.Overview.ConfirmedOrders);
        Assert.Equal(240m, result.Overview.SalesAmount); // No shipping/tax or current product prices.
        Assert.Equal(15, result.Overview.UnitsSold);
        Assert.Equal(48m, result.Overview.AverageOrderValue);
        Assert.Equal(firstId, result.MostAdded[0].ProductId);
        Assert.Equal(2, result.MostAdded[0].CartAdditions);
        Assert.Equal(5, result.MostAdded[0].QuantityAdded);
        Assert.True(result.MostAdded[0].IsDeleted);
        Assert.False(result.MostAdded[0].IsActive);
        Assert.Equal(secondId, result.MostOrdered[0].ProductId);
        Assert.Equal(9, result.MostOrdered[0].UnitsSold);
        Assert.Equal(2, result.MostOrdered[0].OrderCount);
        Assert.False(result.MostOrdered[0].IsActive);
        Assert.Equal(2, result.Performance.TotalCount);
        Assert.Equal(2, result.Performance.TotalPages);
        Assert.Equal(secondId, Assert.Single(result.Performance.Items).ProductId);
        Assert.Equal(7, counter.Commands.Count);
        Assert.Contains(counter.Commands, sql => sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(counter.Commands, sql => sql.Contains("COUNT_BIG", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(counter.Commands, sql => sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("FETCH NEXT", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(context.ChangeTracker.Entries());
        var secondPage = await Service(context).GetAsync(new() { PageSize = 1, PageNumber = 2 });
        Assert.Equal(firstId, Assert.Single(secondPage.Performance.Items).ProductId);
    }

    [SqlServerFact]
    public async Task RollingPeriodsAndCoverage_UsePersistentTrackingStart_NotTheFirstOrLastEvent()
    {
        await using (var setup = database.Context())
        {
            var (user, products) = await SeedProducts(setup, 4);
            var ages = new[] { 1, 10, 45, 120 };
            for (var index = 0; index < ages.Length; index++)
            {
                setup.Orders.Add(Order(user.Id, products[index], index + 1, 10m, OrderStatus.Confirmed, now.AddDays(-ages[index])));
                setup.Set<CartAdditionEvent>().Add(Event(products[index], index + 1, now.AddDays(-ages[index])));
            }
            await setup.SaveChangesAsync();
        }
        await using var context = database.Context();
        foreach (var (days, count, units) in new[] { (7, 1, 1L), (30, 2, 3L), (90, 3, 6L), (0, 4, 10L) })
        {
            var result = await Service(context).GetAsync(new() { PeriodDays = days });
            Assert.Equal(count, result.Overview.TotalOrders);
            Assert.Equal(units, result.Overview.UnitsSold);
            Assert.Equal(units * 10m, result.Overview.SalesAmount);
            Assert.Equal(count, result.Performance.TotalCount);
            Assert.Equal(days != 0, result.CartCoverageComplete);
        }
        await context.Set<AnalyticsTrackingState>().Where(state => state.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.TrackingStartedAtUtc, now.AddDays(-2)));
        var partial = await Service(context).GetAsync(new() { PeriodDays = 7 });
        Assert.False(partial.CartCoverageComplete);
        Assert.Equal(now.AddDays(-2), partial.CartTrackingStartedAtUtc);
        Assert.Single(partial.MostAdded);
        await context.Set<AnalyticsTrackingState>().ExecuteDeleteAsync();
        await Assert.ThrowsAsync<ConflictException>(() => Service(context).GetAsync(new()));
    }

    [SqlServerFact]
    public async Task ActivityBeyondOneHundredProducts_IsPagedOnServer_WithStableTopTenAndTies()
    {
        int[] ids;
        await using (var setup = database.Context())
        {
            var (user, products) = await SeedProducts(setup, 125);
            ids = products.Select(product => product.Id).Order().ToArray();
            foreach (var product in products)
            {
                setup.Orders.Add(Order(user.Id, product, 1, 10m, OrderStatus.Confirmed, now.AddDays(-1)));
                setup.Set<CartAdditionEvent>().Add(Event(product, 1, now.AddDays(-1)));
            }
            await setup.SaveChangesAsync();
        }
        var counter = new Reads();
        await using var context = database.Context(counter);
        var result = await Service(context).GetAsync(new() { PageNumber = 3, PageSize = 50 });
        Assert.Equal(125, result.Performance.TotalCount);
        Assert.Equal(3, result.Performance.TotalPages);
        Assert.Equal(ids.Skip(100), result.Performance.Items.Select(product => product.ProductId));
        Assert.Equal(ids.Take(10), result.MostAdded.Select(product => product.ProductId));
        Assert.Equal(ids.Take(10), result.MostOrdered.Select(product => product.ProductId));
        Assert.Equal(7, counter.Commands.Count); // Fixed query count, independent of product count.
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task QuantityAggregates_UseBigInt_AndDoNotOverflowAtInt32MaxValue()
    {
        await using (var setup = database.Context())
        {
            var (user, products) = await SeedProducts(setup, 1);
            for (var index = 0; index < 2; index++)
            {
                setup.Orders.Add(Order(user.Id, products[0], int.MaxValue, 1m, OrderStatus.Delivered, now.AddDays(-1)));
                setup.Set<CartAdditionEvent>().Add(Event(products[0], int.MaxValue, now.AddDays(-1)));
            }
            await setup.SaveChangesAsync();
        }
        await using var context = database.Context();
        var result = await Service(context).GetAsync(new());
        Assert.Equal((long)int.MaxValue * 2, result.Overview.UnitsSold);
        var row = Assert.Single(result.Performance.Items);
        Assert.Equal((long)int.MaxValue * 2, row.QuantityAdded);
        Assert.Equal((long)int.MaxValue * 2, row.UnitsSold);
        Assert.Equal(2, row.CartAdditions);
        Assert.Equal(2, row.OrderCount);
    }

    private AnalyticsService Service(ApplicationDbContext context) =>
        new(new AnalyticsRepository(context), new AnalyticsQueryDtoValidator(), new FixedTime(now));
    private async Task<(ApplicationUser User, Product[] Products)> SeedProducts(ApplicationDbContext context, int count)
    {
        var category = new Category();
        category.UpdateDetails("Analytics " + Guid.NewGuid().ToString("N"), "Private SQL analytics test", null, true);
        var email = Guid.NewGuid().ToString("N") + "@isolated.example.test";
        var user = new ApplicationUser { FullName = "Analytics test", Email = email, UserName = email, NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant() };
        context.AddRange(category, user);
        await context.SaveChangesAsync();
        var products = Enumerable.Range(0, count).Select(index => Product.Create("Analytics product " + index,
            "Private SQL test product", Guid.NewGuid().ToString("N"), "Test brand", 900m, 800m, 1000, category.Id, true)).ToArray();
        context.Products.AddRange(products);
        await context.SaveChangesAsync();
        await context.Set<AnalyticsTrackingState>().Where(state => state.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.TrackingStartedAtUtc, now.AddDays(-200)));
        return (user, products);
    }
    private static Order Order(string user, Product product, int quantity, decimal price, OrderStatus status, DateTime at)
    {
        var subtotal = quantity * price;
        return new Order
        {
            OrderNumber = "AT-" + Guid.NewGuid().ToString("N")[..28], UserId = user, Status = status, OrderDate = at,
            SubTotal = subtotal, ShippingCost = 25m, TaxAmount = 10m, TotalAmount = subtotal + 35m,
            RecipientName = "Historical recipient", ShippingPhone = "01012345678", ShippingAddress = "Private test address",
            Items = [new OrderItem { ProductId = product.Id, ProductName = "Historical purchased name", SKU = product.SKU,
                Quantity = quantity, UnitPrice = price, LineTotal = subtotal }]
        };
    }
    private static CartAdditionEvent Event(Product product, int quantity, DateTime at) =>
        CartAdditionEvent.Create(product.Id, quantity, new string('A', 64), Guid.NewGuid(), at);
    private sealed class FixedTime(DateTime current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(current);
    }
    private sealed class Reads : DbCommandInterceptor
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
