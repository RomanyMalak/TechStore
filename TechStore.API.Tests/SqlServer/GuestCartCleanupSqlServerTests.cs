using Microsoft.EntityFrameworkCore;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Data;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class GuestCartCleanupSqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task CleanupIsBoundedGuestOnlyAndIdempotent_WithRealCascadeDeletion()
    {
        var scenario = await database.SeedAsync();
        var cutoff = DateTime.UtcNow.AddDays(-30);
        static Cart Guest(DateTime created, DateTime? updated, int productId) => new()
        {
            GuestKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            CreatedAt = created, UpdatedAt = updated, Items = [new CartItem { ProductId = productId, Quantity = 1 }]
        };
        var expiredCreated = Guest(cutoff.AddDays(-10), null, scenario.ProductId);
        var expiredUpdated = Guest(cutoff.AddDays(-10), cutoff.AddDays(-1), scenario.ProductId);
        var activeUpdated = Guest(cutoff.AddDays(-10), cutoff.AddDays(1), scenario.ProductId);
        var recent = Guest(cutoff.AddDays(1), null, scenario.ProductId);
        var atBoundary = Guest(cutoff, null, scenario.ProductId);
        await using (var setup = database.Context())
        {
            var customer = await setup.Carts.SingleAsync(cart => cart.UserId == scenario.UserId);
            customer.CreatedAt = cutoff.AddDays(-100);
            setup.Carts.AddRange(expiredCreated, expiredUpdated, activeUpdated, recent, atBoundary);
            await setup.SaveChangesAsync();
        }
        await using (var cleanup = database.Context())
        {
            var service = new GuestCartCleanup(cleanup);
            Assert.Equal(1, await service.DeleteExpiredBatchAsync(cutoff, 1));
            Assert.Equal(1, await service.DeleteExpiredBatchAsync(cutoff, 1));
            Assert.Equal(0, await service.DeleteExpiredBatchAsync(cutoff, 1));
        }
        await using var verify = database.Context();
        var expired = new[] { expiredCreated.Id, expiredUpdated.Id };
        Assert.False(await verify.Carts.AnyAsync(cart => expired.Contains(cart.Id)));
        Assert.False(await verify.CartItems.AnyAsync(item => expired.Contains(item.CartId)));
        var preserved = new[] { activeUpdated.Id, recent.Id, atBoundary.Id };
        Assert.Equal(3, await verify.Carts.CountAsync(cart => preserved.Contains(cart.Id)));
        Assert.True(await verify.Carts.AnyAsync(cart => cart.UserId == scenario.UserId));
        Assert.Equal(4, await verify.CartItems.CountAsync(item => item.ProductId == scenario.ProductId));
        Assert.Equal(scenario.InitialStock, (await verify.Products.FindAsync(scenario.ProductId))!.Stock);
    }
}
