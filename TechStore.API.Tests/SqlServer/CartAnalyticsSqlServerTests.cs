using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Mapping;
using TechStore.Application.Services;
using TechStore.Application.Validators.Commerce;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class CartAnalyticsSqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task SuccessfulAdds_PersistRequestedDeltasAndOwnerHash_WithTheCartTransaction()
    {
        var scenario = await database.SeedAsync();
        var owner = CartOwner.User(scenario.UserId);
        var occurred = DateTime.UtcNow;
        var first = new AddToCartDto { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = Guid.NewGuid() };
        var second = new AddToCartDto { ProductId = scenario.ProductId, Quantity = 3, ClientRequestId = Guid.NewGuid() };
        await using (var context = database.Context())
            Assert.Equal(3, (await Service(new CommerceRepository(context), occurred).AddAsync(owner, first)).TotalQuantity);
        await using (var context = database.Context())
            Assert.Equal(6, (await Service(new CommerceRepository(context), occurred).AddAsync(owner, second)).TotalQuantity);

        await using var verification = database.Context();
        var events = await verification.CartAdditionEvents.Where(entry => entry.ProductId == scenario.ProductId).OrderBy(entry => entry.Id).ToArrayAsync();
        Assert.Equal(new[] { 2, 3 }, events.Select(entry => entry.Quantity));
        Assert.Equal(new[] { first.ClientRequestId!.Value, second.ClientRequestId!.Value }, events.Select(entry => entry.ClientRequestId));
        Assert.All(events, entry =>
        {
            Assert.Equal(CommerceMapping.Hash("user:" + scenario.UserId), entry.OwnerHash);
            Assert.Equal(occurred, entry.OccurredAtUtc);
        });
        Assert.Equal(6, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
        Assert.Equal(scenario.InitialStock, (await verification.Products.FindAsync(scenario.ProductId))!.Stock);
    }

    [SqlServerFact]
    public async Task LegacyAddsWithoutRequestIds_StillWorkAndReceiveDistinctEventIdentities()
    {
        var scenario = await database.SeedAsync();
        var dto = new AddToCartDto { ProductId = scenario.ProductId };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = database.Context();
            await Service(new CommerceRepository(context)).AddAsync(CartOwner.User(scenario.UserId), dto);
        }
        await using var verification = database.Context();
        var events = await verification.CartAdditionEvents.Where(entry => entry.ProductId == scenario.ProductId).ToArrayAsync();
        Assert.Equal(2, events.Length);
        Assert.Equal(2, events.Select(entry => entry.ClientRequestId).Distinct().Count());
        Assert.All(events, entry => Assert.NotEqual(Guid.Empty, entry.ClientRequestId));
        Assert.Equal(3, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
    }

    [SqlServerFact]
    public async Task ACommittedRetry_DoesNotReAddAfterRemoval_OrRevalidateLaterProductAvailability()
    {
        var scenario = await database.SeedAsync();
        var owner = CartOwner.User(scenario.UserId);
        var dto = new AddToCartDto { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = Guid.NewGuid() };
        await using (var context = database.Context())
        {
            var service = Service(new CommerceRepository(context));
            await service.AddAsync(owner, dto);
            await service.RemoveAsync(owner, scenario.ProductId);
            var product = (await context.Products.FindAsync(scenario.ProductId))!;
            product.SoftDelete();
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
            Assert.Empty((await Service(new CommerceRepository(context)).AddAsync(owner, dto)).Items);

        await using var verification = database.Context();
        Assert.Equal(1, await verification.CartAdditionEvents.CountAsync(entry => entry.ProductId == scenario.ProductId));
        Assert.False(await verification.CartItems.AnyAsync(item => item.ProductId == scenario.ProductId));
    }

    [SqlServerFact]
    public async Task ReusingARequestForDifferentQuantityOrProduct_ConflictsWithoutNewMutation()
    {
        var scenario = await database.SeedAsync();
        var owner = CartOwner.User(scenario.UserId);
        var request = Guid.NewGuid();
        await using (var context = database.Context())
            await Service(new CommerceRepository(context)).AddAsync(owner, new() { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = request });
        foreach (var dto in new[]
        {
            new AddToCartDto { ProductId = scenario.ProductId, Quantity = 3, ClientRequestId = request },
            new AddToCartDto { ProductId = int.MaxValue, Quantity = 2, ClientRequestId = request }
        })
        {
            await using var context = database.Context();
            var conflict = await Assert.ThrowsAsync<CommerceConflictException>(() => Service(new CommerceRepository(context)).AddAsync(owner, dto));
            Assert.Equal("IdempotencyMismatch", conflict.Code);
        }
        await using var verification = database.Context();
        Assert.Equal(1, await verification.CartAdditionEvents.CountAsync(entry => entry.ProductId == scenario.ProductId));
        Assert.Equal(3, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
    }

    [SqlServerFact]
    public async Task TheSameClientRequestId_IsIndependentForTwoCustomersAndAGuest()
    {
        var scenario = await database.SeedAsync();
        var secondUser = await database.AddCustomerCartAsync(scenario.ProductId);
        var guestKey = CommerceMapping.Hash(Guid.NewGuid().ToString("N"));
        var owners = new[] { CartOwner.User(scenario.UserId), CartOwner.User(secondUser), CartOwner.Guest(guestKey) };
        var dto = new AddToCartDto { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = Guid.NewGuid() };
        foreach (var owner in owners)
        {
            await using var context = database.Context();
            await Service(new CommerceRepository(context)).AddAsync(owner, dto);
        }
        await using var verification = database.Context();
        var events = await verification.CartAdditionEvents.Where(entry => entry.ProductId == scenario.ProductId).ToArrayAsync();
        Assert.Equal(3, events.Length);
        Assert.Equal(3, events.Select(entry => entry.OwnerHash).Distinct().Count());
        Assert.All(events, entry => Assert.Equal(dto.ClientRequestId!.Value, entry.ClientRequestId));
        Assert.Contains(events, entry => entry.OwnerHash == CommerceMapping.Hash("guest:" + guestKey));
        Assert.Equal(new[] { 2, 3, 3 }, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).OrderBy(item => item.Quantity).Select(item => item.Quantity).ToArrayAsync());
    }

    [SqlServerFact]
    public async Task ReadsQuantityUpdatesRemovalsAndGuestMerges_DoNotGenerateAdditionEvents()
    {
        var scenario = await database.SeedAsync();
        var owner = CartOwner.User(scenario.UserId);
        var guestKey = CommerceMapping.Hash(Guid.NewGuid().ToString("N"));
        await using (var context = database.Context())
        {
            var service = Service(new CommerceRepository(context));
            await service.GetAsync(owner);
            await service.GetAsync(owner);
            await service.UpdateAsync(owner, scenario.ProductId, new() { Quantity = 2 });
            await service.RemoveAsync(owner, scenario.ProductId);
            Assert.False(await context.CartAdditionEvents.AnyAsync(entry => entry.ProductId == scenario.ProductId));
        }
        await using (var context = database.Context())
            await Service(new CommerceRepository(context)).AddAsync(CartOwner.Guest(guestKey), new() { ProductId = scenario.ProductId, Quantity = 2 });
        await using (var context = database.Context())
        {
            var service = Service(new CommerceRepository(context));
            Assert.Equal(2, (await service.MergeAsync(scenario.UserId, guestKey)).TotalQuantity);
            await service.MergeAsync(scenario.UserId, guestKey);
            await service.GetAsync(owner);
        }
        await using var verification = database.Context();
        var entry = await verification.CartAdditionEvents.SingleAsync(entry => entry.ProductId == scenario.ProductId);
        Assert.Equal(CommerceMapping.Hash("guest:" + guestKey), entry.OwnerHash);
        Assert.Equal(2, entry.Quantity);
        Assert.False(await verification.Carts.AnyAsync(cart => cart.GuestKey == guestKey));
    }

    [SqlServerFact]
    public async Task InvalidMissingOutOfStockAndUnavailableAdds_DoNotPersistEvents()
    {
        var scenario = await database.SeedAsync(stock: 1);
        var owner = CartOwner.User(scenario.UserId);
        await using (var context = database.Context())
            await Assert.ThrowsAsync<TechStore.Application.Exceptions.ValidationException>(() => Service(new CommerceRepository(context))
                .AddAsync(owner, new() { ProductId = scenario.ProductId, Quantity = 0, ClientRequestId = Guid.NewGuid() }));
        await using (var context = database.Context())
            await Assert.ThrowsAsync<NotFoundException>(() => Service(new CommerceRepository(context))
                .AddAsync(owner, new() { ProductId = int.MaxValue, ClientRequestId = Guid.NewGuid() }));
        await using (var context = database.Context())
        {
            var conflict = await Assert.ThrowsAsync<CommerceConflictException>(() => Service(new CommerceRepository(context))
                .AddAsync(owner, new() { ProductId = scenario.ProductId, ClientRequestId = Guid.NewGuid() }));
            Assert.Equal("StockUnavailable", conflict.Code);
            (await context.Products.FindAsync(scenario.ProductId))!.SoftDelete();
            await context.SaveChangesAsync();
        }
        await using (var context = database.Context())
        {
            var conflict = await Assert.ThrowsAsync<CommerceConflictException>(() => Service(new CommerceRepository(context))
                .AddAsync(owner, new() { ProductId = scenario.ProductId, ClientRequestId = Guid.NewGuid() }));
            Assert.Equal("ProductUnavailable", conflict.Code);
        }
        await using var verification = database.Context();
        Assert.False(await verification.CartAdditionEvents.AnyAsync(entry => entry.ProductId == scenario.ProductId));
        Assert.Equal(1, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
    }

    [SqlServerFact]
    public async Task FailureAfterSqlSave_RollsBackBothCartAndEvent_AndTheSameRequestCanRetry()
    {
        var scenario = await database.SeedAsync();
        var dto = new AddToCartDto { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = Guid.NewGuid() };
        var failure = new FailAfterSave();
        await using (var context = database.Context(failure))
            await Assert.ThrowsAsync<InjectedFailure>(() => Service(new CommerceRepository(context)).AddAsync(CartOwner.User(scenario.UserId), dto));
        Assert.True(failure.SqlSaveCompleted);
        await using (var verification = database.Context())
        {
            Assert.False(await verification.CartAdditionEvents.AnyAsync(entry => entry.ProductId == scenario.ProductId));
            Assert.Equal(1, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
        }
        await using (var context = database.Context())
            Assert.Equal(3, (await Service(new CommerceRepository(context)).AddAsync(CartOwner.User(scenario.UserId), dto)).TotalQuantity);
        await using var committed = database.Context();
        Assert.Equal(dto.ClientRequestId!.Value, (await committed.CartAdditionEvents.SingleAsync(entry => entry.ProductId == scenario.ProductId)).ClientRequestId);
    }

    [SqlServerFact]
    public async Task ConcurrentDuplicateAdds_ConflictThenReplay_CommitsOneDeltaAndOneEvent()
    {
        var scenario = await database.SeedAsync();
        var dto = new AddToCartDto { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = Guid.NewGuid() };
        var gate = new ReadGate();
        var outcomes = await Task.WhenAll(AddConcurrent(scenario, dto, gate), AddConcurrent(scenario, dto, gate));
        Assert.Single(outcomes, outcome => outcome.Cart is not null);
        Assert.Single(outcomes, outcome => outcome.Conflict is not null);
        var replay = await AddConcurrent(scenario, dto);
        Assert.Null(replay.Conflict);
        Assert.Equal(3, replay.Cart!.TotalQuantity);
        await using var verification = database.Context();
        Assert.Equal(1, await verification.CartAdditionEvents.CountAsync(entry => entry.ProductId == scenario.ProductId));
        Assert.Equal(3, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
    }

    [SqlServerFact]
    public async Task ConcurrentDifferentAdds_DeadlockRetry_PreservesBothDeltasAndExactlyTwoEvents()
    {
        var scenario = await database.SeedAsync();
        var requests = new[]
        {
            new AddToCartDto { ProductId = scenario.ProductId, Quantity = 2, ClientRequestId = Guid.NewGuid() },
            new AddToCartDto { ProductId = scenario.ProductId, Quantity = 3, ClientRequestId = Guid.NewGuid() }
        };
        var gate = new ReadGate();
        var outcomes = await Task.WhenAll(requests.Select(dto => AddConcurrent(scenario, dto, gate)));
        Assert.Single(outcomes, outcome => outcome.Cart is not null);
        var failed = Array.FindIndex(outcomes, outcome => outcome.Conflict is not null);
        Assert.True(failed >= 0);
        Assert.Null((await AddConcurrent(scenario, requests[failed])).Conflict);
        // Repeating both original requests is safe after the loser succeeds.
        foreach (var request in requests) Assert.Equal(6, (await AddConcurrent(scenario, request)).Cart!.TotalQuantity);
        await using var verification = database.Context();
        var events = await verification.CartAdditionEvents.Where(entry => entry.ProductId == scenario.ProductId).ToArrayAsync();
        Assert.Equal(2, events.Length);
        Assert.Equal(5, events.Sum(entry => entry.Quantity));
        Assert.Equal(6, await verification.CartItems.Where(item => item.ProductId == scenario.ProductId).Select(item => item.Quantity).SingleAsync());
    }

    [SqlServerFact]
    public async Task SqlConstraints_RejectInvalidEventsAndDuplicateRequestKeys()
    {
        var scenario = await database.SeedAsync();
        var ownerHash = CommerceMapping.Hash("user:" + scenario.UserId);
        await using var context = database.Context();
        foreach (var (quantity, hash, request) in new[]
        {
            (0, ownerHash, Guid.NewGuid()),
            (1, "SHORT", Guid.NewGuid()),
            (1, new string('Z', 64), Guid.NewGuid()),
            (1, ownerHash, Guid.Empty)
        })
        {
            var time = DateTime.UtcNow;
            var failure = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [CartAdditionEvents] ([ProductId], [Quantity], [OwnerHash], [ClientRequestId], [OccurredAtUtc]) VALUES ({scenario.ProductId}, {quantity}, {hash}, {request}, {time})"));
            Assert.Equal(547, failure.Number);
        }
        var requestId = Guid.NewGuid();
        context.CartAdditionEvents.Add(CartAdditionEvent.Create(scenario.ProductId, 1, ownerHash, requestId, DateTime.UtcNow));
        await context.SaveChangesAsync();
        context.CartAdditionEvents.Add(CartAdditionEvent.Create(scenario.ProductId, 1, ownerHash, requestId, DateTime.UtcNow));
        await Assert.ThrowsAsync<ConflictException>(() => context.SaveChangesAsync());
        await using var verification = database.Context();
        Assert.Equal(1, await verification.CartAdditionEvents.CountAsync(entry => entry.ProductId == scenario.ProductId));
    }

    [SqlServerFact]
    public async Task ReviewScript_AppliesOnAnExistingCatalogAndRepeatsSafely_WithoutBackfillOrResettingStartTime()
    {
        // This separate GUID database is the only database downgraded to the old baseline.
        await using var owned = new OwnedDatabase();
        await owned.Database.InitializeAsync();
        await using (var baseline = owned.Database.Context())
            await baseline.GetService<IMigrator>().MigrateAsync("20261107233344_AddGuestCartsAndOrderIdempotency");
        var legacy = await owned.Database.SeedAsync();
        await using var context = owned.Database.Context();
        var before = await SqlUtcNow(context);
        var script = await File.ReadAllTextAsync(Path.Combine(SolutionRoot(), "docs", "migrations", "add-cart-addition-analytics.sql"));
        Assert.Contains("SYSUTCDATETIME()", script);
        Assert.Contains("IF NOT EXISTS", script);
        await ExecuteScript(context, script);
        var after = await SqlUtcNow(context);
        var firstStart = (await context.AnalyticsTrackingStates.AsNoTracking().SingleAsync()).TrackingStartedAtUtc;
        Assert.InRange(firstStart, before, after);
        Assert.False(await context.CartAdditionEvents.AnyAsync());
        Assert.Equal(1, await context.CartItems.Where(item => item.ProductId == legacy.ProductId).Select(item => item.Quantity).SingleAsync());
        await ExecuteScript(context, script);
        Assert.Equal(firstStart, (await context.AnalyticsTrackingStates.AsNoTracking().SingleAsync()).TrackingStartedAtUtc);
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(await context.CartAdditionEvents.AnyAsync());
    }

    private static CartService Service(ICommerceRepository repository, DateTime? time = null) =>
        new(repository, new AddToCartValidator(), new UpdateCartItemValidator(), time.HasValue ? new FixedClock(time.Value) : TimeProvider.System);
    private async Task<Outcome> AddConcurrent(SqlServerFixture.Scenario scenario, AddToCartDto dto, ReadGate? gate = null)
    {
        await using var context = database.Context();
        try { return new(await Service(new GatedRepository(new CommerceRepository(context), gate)).AddAsync(CartOwner.User(scenario.UserId), dto), null); }
        catch (Exception exception) when (exception is ConflictException or CommerceConflictException) { return new(null, exception); }
    }
    private sealed record Outcome(CartResponseDto? Cart, Exception? Conflict);
    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
    private sealed class ReadGate
    {
        private int arrivals;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Arrive(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
    }
    private sealed class GatedRepository(ICommerceRepository inner, ReadGate? gate) : ICommerceRepository
    {
        public Task<Cart?> GetCartAsync(CartOwner owner, bool tracked, CancellationToken token) => inner.GetCartAsync(owner, tracked, token);
        public async Task<CartAdditionEvent?> GetCartAdditionByRequestAsync(string ownerHash, Guid requestId, CancellationToken token)
        {
            var result = await inner.GetCartAdditionByRequestAsync(ownerHash, requestId, token);
            if (gate is not null) await gate.Arrive(token);
            return result;
        }
        public Task AddCartAdditionAsync(CartAdditionEvent addition, CancellationToken token) => inner.AddCartAdditionAsync(addition, token);
        public Task<Product?> GetProductAsync(int id, CancellationToken token) => inner.GetProductAsync(id, token);
        public Task<Address?> GetAddressAsync(string user, int id, CancellationToken token) => inner.GetAddressAsync(user, id, token);
        public Task<Order?> GetOrderAsync(string user, int id, CancellationToken token) => inner.GetOrderAsync(user, id, token);
        public Task<Order?> GetOrderByRequestAsync(string user, Guid request, CancellationToken token) => inner.GetOrderByRequestAsync(user, request, token);
        public Task AddCartAsync(Cart cart, CancellationToken token) => inner.AddCartAsync(cart, token);
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
    private sealed class OwnedDatabase : IAsyncDisposable
    {
        public SqlServerFixture Database { get; } = new();
        public ValueTask DisposeAsync() => new(Database.DisposeAsync());
    }
    private static Task<DateTime> SqlUtcNow(TechStore.Infrastructure.Persistence.ApplicationDbContext context) =>
        context.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
    private static async Task ExecuteScript(TechStore.Infrastructure.Persistence.ApplicationDbContext context, string script)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*(?:--[^\r\n]*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 60;
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechStore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate the solution for the reviewed migration script.");
    }
}
