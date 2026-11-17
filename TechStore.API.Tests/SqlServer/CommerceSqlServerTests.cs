using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Services;
using TechStore.Application.Validators.Commerce;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Persistence;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class CommerceSqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task ConcurrentPurchaseOfLastItem_OneOrderAndNoOverselling()
    {
        var scenario = await database.SeedAsync(stock: 1);
        var otherUser = await database.AddCustomerCartAsync(scenario.ProductId);
        var first = await RequestAsync(scenario.UserId);
        var second = await RequestAsync(otherUser);
        var gate = new ReadGate();
        var results = await Task.WhenAll(PlaceAsync(scenario.UserId, first, gate), PlaceAsync(otherUser, second, gate));
        Assert.Single(results, result => result.Order is not null);
        Assert.Single(results, result => result.Conflict is not null);
        await using var verification = database.Context();
        Assert.Equal(0, (await verification.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Equal(1, await verification.OrderItems.Where(item => item.ProductId == scenario.ProductId).SumAsync(item => item.Quantity));
        Assert.Equal(1, await verification.Orders.CountAsync(order => order.UserId == scenario.UserId || order.UserId == otherUser));
        Assert.Equal(1, await verification.CartItems.CountAsync(item => item.ProductId == scenario.ProductId));
    }

    [SqlServerFact]
    public async Task ConcurrentCartAdds_ConflictThenRetryPreservesBothChanges()
    {
        var scenario = await database.SeedAsync();
        var gate = new ReadGate();
        async Task<bool> Add(int quantity, ReadGate? synchronization)
        {
            await using var context = database.Context();
            var repository = new GatedRepository(new CommerceRepository(context), synchronization);
            try
            {
                await new CartService(repository, new AddToCartValidator(), new UpdateCartItemValidator())
                    .AddAsync(CartOwner.User(scenario.UserId), new AddToCartDto { ProductId = scenario.ProductId, Quantity = quantity });
                return true;
            }
            catch (ConflictException) { return false; }
        }
        var results = await Task.WhenAll(Add(2, gate), Add(3, gate));
        Assert.Single(results, success => success);
        Assert.True(await Add(results[0] ? 3 : 2, null));
        await using var verification = database.Context();
        Assert.Equal(6, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
        Assert.Equal(scenario.InitialStock, (await verification.Products.FindAsync(scenario.ProductId))!.Stock);
    }

    [SqlServerFact]
    public async Task ConcurrentDifferentCheckoutRequests_DeductAndClearOnlyOnce()
    {
        var scenario = await database.SeedAsync();
        var requests = new[] { await RequestAsync(scenario.UserId), await RequestAsync(scenario.UserId) };
        var gate = new ReadGate();
        var results = await Task.WhenAll(requests.Select(request => PlaceAsync(scenario.UserId, request, gate)));
        Assert.Single(results, result => result.Order is not null);
        var failed = Array.FindIndex(results, result => result.Conflict is not null);
        Assert.True(failed >= 0);
        var replay = await PlaceAsync(scenario.UserId, requests[failed]);
        Assert.IsType<CommerceConflictException>(replay.Conflict);
        Assert.Equal("EmptyCart", ((CommerceConflictException)replay.Conflict!).Code);
        await AssertCommittedOnce(scenario);
    }

    [SqlServerFact]
    public async Task ConcurrentSameRequest_IdempotentRetryReturnsSameOrder()
    {
        var scenario = await database.SeedAsync();
        var request = await RequestAsync(scenario.UserId);
        var gate = new ReadGate();
        var results = await Task.WhenAll(PlaceAsync(scenario.UserId, request, gate), PlaceAsync(scenario.UserId, request, gate));
        var success = Assert.Single(results, result => result.Order is not null).Order!;
        var replay = await PlaceAsync(scenario.UserId, request);
        Assert.Null(replay.Conflict);
        Assert.Equal(success.Id, replay.Order!.Id);
        await AssertCommittedOnce(scenario);
    }

    [SqlServerFact]
    public async Task FailureAfterSqlSave_RollsBackOrderStockCartAndHistory()
    {
        var scenario = await database.SeedAsync();
        var request = await RequestAsync(scenario.UserId);
        var failure = new FailAfterSave();
        await using (var context = database.Context(failure))
        {
            await Assert.ThrowsAsync<InjectedFailure>(() => Checkout(new CommerceRepository(context)).CreateOrderAsync(scenario.UserId, request));
        }
        Assert.True(failure.SqlSaveCompleted);
        await using var verification = database.Context();
        Assert.Equal(scenario.InitialStock, (await verification.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Equal(1, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
        Assert.False(await verification.Orders.AnyAsync(order => order.UserId == scenario.UserId));
        Assert.False(await verification.OrderItems.AnyAsync(item => item.ProductId == scenario.ProductId));
        Assert.False(await verification.OrderStatusHistories.AnyAsync(history => history.ChangedByUserId == scenario.UserId));
    }

    private async Task AssertCommittedOnce(SqlServerFixture.Scenario scenario)
    {
        await using var context = database.Context();
        Assert.Equal(scenario.InitialStock - 1, (await context.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Single(await context.Orders.Where(order => order.UserId == scenario.UserId).ToListAsync());
        Assert.Single(await context.OrderItems.Where(item => item.ProductId == scenario.ProductId).ToListAsync());
        Assert.False(await context.CartItems.AnyAsync(item => item.ProductId == scenario.ProductId));
        Assert.Equal(1, await context.OrderStatusHistories.CountAsync(history => history.ChangedByUserId == scenario.UserId));
    }

    private async Task<CreateOrderDto> RequestAsync(string userId)
    {
        await using var context = database.Context();
        var quote = await Checkout(new CommerceRepository(context)).GetQuoteAsync(userId);
        return new CreateOrderDto { ClientRequestId = Guid.NewGuid(), QuoteVersion = quote.Version,
            ShippingAddress = new() { FullName = "SQL customer", Phone = "01012345678", City = "Cairo", Street = "Isolated test street", Building = "1" } };
    }

    private async Task<Outcome> PlaceAsync(string userId, CreateOrderDto request, ReadGate? gate = null)
    {
        await using var context = database.Context();
        try { return new(await Checkout(new GatedRepository(new CommerceRepository(context), gate)).CreateOrderAsync(userId, request), null); }
        catch (Exception conflict) when (conflict is ConflictException or CommerceConflictException) { return new(null, conflict); }
    }

    private static CheckoutService Checkout(ICommerceRepository repository) => new(repository, new CreateOrderValidator(), new CheckoutPolicy(0));
    private sealed record Outcome(OrderResponseDto? Order, Exception? Conflict);

    // Both real SQL transactions must read before either writes: this forces meaningful lock contention.
    internal sealed class ReadGate
    {
        private int arrivals;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    private sealed class GatedRepository(ICommerceRepository inner, ReadGate? gate) : ICommerceRepository
    {
        public async Task<Cart?> GetCartAsync(CartOwner owner, bool tracked, CancellationToken token)
        {
            var cart = await inner.GetCartAsync(owner, tracked, token);
            if (tracked && gate is not null) await gate.ArriveAsync(token);
            return cart;
        }
        public Task<Product?> GetProductAsync(int id, CancellationToken token) => inner.GetProductAsync(id, token);
        public Task<Address?> GetAddressAsync(string user, int id, CancellationToken token) => inner.GetAddressAsync(user, id, token);
        public Task<Order?> GetOrderAsync(string user, int id, CancellationToken token) => inner.GetOrderAsync(user, id, token);
        public Task<Order?> GetOrderByRequestAsync(string user, Guid request, CancellationToken token) => inner.GetOrderByRequestAsync(user, request, token);
        public Task AddCartAsync(Cart cart, CancellationToken token) => inner.AddCartAsync(cart, token);
        public Task<CartAdditionEvent?> GetCartAdditionByRequestAsync(string ownerHash, Guid request, CancellationToken token) => inner.GetCartAdditionByRequestAsync(ownerHash, request, token);
        public Task AddCartAdditionAsync(CartAdditionEvent addition, CancellationToken token) => inner.AddCartAdditionAsync(addition, token);
        public void RemoveCart(Cart cart) => inner.RemoveCart(cart);
        public Task AddOrderAsync(Order order, CancellationToken token) => inner.AddOrderAsync(order, token);
        public Task<int> SaveChangesAsync(CancellationToken token) => inner.SaveChangesAsync(token);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) => inner.ExecuteInTransactionAsync(action, token);
    }

    private sealed class InjectedFailure : Exception;
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool SqlSaveCompleted { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            SqlSaveCompleted = true;
            throw new InjectedFailure();
        }
    }
}
