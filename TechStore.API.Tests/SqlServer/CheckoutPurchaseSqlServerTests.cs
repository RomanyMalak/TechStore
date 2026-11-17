using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Services;
using TechStore.Application.Validators.Commerce;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

// Each HTTP request gets the real scoped repository; all persistence assertions use
// another DbContext against the fixture-owned GUID database, never the live catalog.
[Collection(SqlServerCollection.Name)]
public sealed class CheckoutPurchaseSqlServerTests(SqlServerFixture database)
{
    private const decimal Shipping = 4.95m;

    [SqlServerFact]
    public async Task FirstThenSecondPurchase_ClearsSqlCartAndPersistsIndependentCurrentPriceSnapshots()
    {
        var products = await ProductsAsync();
        await using var factory = new SqlApiFactory(database, checkoutPolicy: new(Shipping));
        var customer = await factory.CustomerAsync();
        using var client = customer.Client;

        await AddAsync(client, products.FirstId, 2);
        var firstQuote = await QuoteAsync(client);
        Assert.Equal(products.FirstId, Assert.Single(firstQuote.Cart.Items).ProductId);
        Assert.Equal(39.98m, firstQuote.Cart.OriginalSubTotal);
        Assert.Equal(5.28m, firstQuote.Cart.DiscountTotal);
        Assert.Equal(34.70m, firstQuote.Cart.SubTotal);
        Assert.Equal(39.65m, firstQuote.TotalAmount);
        var firstRequest = Request(firstQuote.Version);
        var first = await PlaceAsync(client, firstRequest);
        Assert.Equal(39.65m, first.TotalAmount);
        await AssertEmptyCartAsync(client, customer.User.Id);

        // Change the current catalog after purchase. Order A must retain its
        // original prices, while the next quote must use Product B's new price.
        await using (var context = database.Context())
        {
            var firstProduct = (await context.Products.FindAsync(products.FirstId))!;
            firstProduct.UpdateDetails(firstProduct.Name, firstProduct.Description, firstProduct.SKU,
                firstProduct.Brand, 25.00m, 23.00m, firstProduct.Stock, firstProduct.CategoryId, true);
            var secondProduct = (await context.Products.FindAsync(products.SecondId))!;
            secondProduct.UpdateDetails(secondProduct.Name, secondProduct.Description, secondProduct.SKU,
                secondProduct.Brand, 8.35m, 7.25m, secondProduct.Stock, secondProduct.CategoryId, true);
            await context.SaveChangesAsync();
        }

        await AddAsync(client, products.SecondId, 3);
        var secondCart = (await client.GetFromJsonAsync<CartResponseDto>("/api/cart"))!;
        Assert.Equal(products.SecondId, Assert.Single(secondCart.Items).ProductId);
        var secondQuote = await QuoteAsync(client);
        Assert.Equal(products.SecondId, Assert.Single(secondQuote.Cart.Items).ProductId);
        Assert.Equal(25.05m, secondQuote.Cart.OriginalSubTotal);
        Assert.Equal(3.30m, secondQuote.Cart.DiscountTotal);
        Assert.Equal(21.75m, secondQuote.Cart.SubTotal);
        Assert.Equal(26.70m, secondQuote.TotalAmount);
        var secondRequest = Request(secondQuote.Version);
        Assert.NotEqual(firstRequest.ClientRequestId, secondRequest.ClientRequestId);
        var second = await PlaceAsync(client, secondRequest);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(products.SecondId, Assert.Single(second.Items).ProductId);
        Assert.Equal(7.25m, second.Items[0].UnitPrice);
        Assert.Equal(26.70m, second.TotalAmount);
        await AssertEmptyCartAsync(client, customer.User.Id);

        // Reading the success/order resources again must be a read, not a cart
        // restore, and order snapshots must survive the later catalog changes.
        for (var refresh = 0; refresh < 2; refresh++)
        {
            var persisted = (await client.GetFromJsonAsync<OrderResponseDto>($"/api/orders/{first.Id}"))!;
            Assert.Equal(39.65m, persisted.TotalAmount);
            Assert.Equal(17.35m, Assert.Single(persisted.Items).UnitPrice);
            Assert.Equal(first.Id, (await client.GetFromJsonAsync<OrderResponseDto>(
                $"/api/orders/by-request/{firstRequest.ClientRequestId}"))!.Id);
        }
        await AssertEmptyCartAsync(client, customer.User.Id);
        await using var verification = database.Context();
        var orders = await verification.Orders.Include(order => order.Items)
            .Where(order => order.UserId == customer.User.Id).OrderBy(order => order.Id).ToArrayAsync();
        Assert.Equal(new[] { first.Id, second.Id }, orders.Select(order => order.Id));
        Assert.Equal(products.FirstId, Assert.Single(orders[0].Items).ProductId);
        Assert.Equal(products.SecondId, Assert.Single(orders[1].Items).ProductId);
        Assert.Equal(products.FirstStock - 2, (await verification.Products.FindAsync(products.FirstId))!.Stock);
        Assert.Equal(products.SecondStock - 3, (await verification.Products.FindAsync(products.SecondId))!.Stock);
    }

    [SqlServerFact]
    public async Task MultipleProducts_QuantityChangesAndDecimalTotalsComeFromSqlRatherThanSubmittedAmounts()
    {
        var products = await ProductsAsync();
        await using var factory = new SqlApiFactory(database, checkoutPolicy: new(Shipping));
        var customer = await factory.CustomerAsync();
        using var client = customer.Client;
        await AddAsync(client, products.FirstId);
        await AddAsync(client, products.SecondId, 2);
        (await client.PutAsJsonAsync($"/api/cart/items/{products.FirstId}", new UpdateCartItemDto { Quantity = 3 })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/cart/items/{products.SecondId}", new UpdateCartItemDto { Quantity = 7 })).EnsureSuccessStatusCode();
        var quote = await QuoteAsync(client);
        Assert.Equal(2, quote.Cart.Items.Count);
        Assert.Equal(10, quote.Cart.TotalQuantity);
        Assert.Equal(62.00m, quote.Cart.OriginalSubTotal);
        Assert.Equal(7.92m, quote.Cart.DiscountTotal);
        Assert.Equal(54.08m, quote.Cart.SubTotal);
        Assert.Equal(Shipping, quote.ShippingCost);
        Assert.Equal(0m, quote.TaxAmount);
        Assert.Equal(59.03m, quote.TotalAmount);
        Assert.Equal(52.05m, quote.Cart.Items.Single(item => item.ProductId == products.FirstId).LineTotal);
        Assert.Equal(2.03m, quote.Cart.Items.Single(item => item.ProductId == products.SecondId).LineTotal);

        var request = Request(quote.Version);
        // Extra client financial fields are not part of CreateOrderDto. The
        // existing endpoint must calculate all payable amounts itself.
        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            request.ClientRequestId, request.QuoteVersion, request.ShippingAddress, request.PaymentMethod,
            SubTotal = 0.01m, ShippingCost = 0m, TaxAmount = 999m, TotalAmount = 0.01m,
            Items = new[] { new { ProductId = products.FirstId, Quantity = 1, UnitPrice = 0.01m, LineTotal = 0.01m } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponseDto>())!;
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(54.08m, order.SubTotal);
        Assert.Equal(Shipping, order.ShippingCost);
        Assert.Equal(0m, order.TaxAmount);
        Assert.Equal(59.03m, order.TotalAmount);
        Assert.Equal(17.35m, order.Items.Single(item => item.ProductId == products.FirstId).UnitPrice);
        Assert.Equal(3, order.Items.Single(item => item.ProductId == products.FirstId).Quantity);
        Assert.Equal(0.29m, order.Items.Single(item => item.ProductId == products.SecondId).UnitPrice);
        Assert.Equal(7, order.Items.Single(item => item.ProductId == products.SecondId).Quantity);
        await AssertEmptyCartAsync(client, customer.User.Id);
        await using var verification = database.Context();
        var persisted = await verification.Orders.Include(value => value.Items).SingleAsync(value => value.Id == order.Id);
        Assert.Equal(59.03m, persisted.TotalAmount);
        Assert.Equal(54.08m, persisted.Items.Sum(item => item.LineTotal));
        Assert.Equal(products.FirstStock - 3, (await verification.Products.FindAsync(products.FirstId))!.Stock);
        Assert.Equal(products.SecondStock - 7, (await verification.Products.FindAsync(products.SecondId))!.Stock);
    }

    [SqlServerFact]
    public async Task FailureAfterSqlSave_RetainsEveryCartItemAndSameRequestCanSafelyRetry()
    {
        var products = await ProductsAsync();
        var owner = CartOwner.User(products.UserId);
        await using (var context = database.Context())
        {
            var cart = CartService(context);
            await cart.AddAsync(owner, new AddToCartDto { ProductId = products.FirstId, Quantity = 3 });
            await cart.AddAsync(owner, new AddToCartDto { ProductId = products.SecondId, Quantity = 7 });
        }
        CreateOrderDto request;
        await using (var context = database.Context())
            request = Request((await Checkout(context).GetQuoteAsync(products.UserId)).Version);
        var failure = new FailAfterOrderSqlSave();
        await using (var context = database.Context(failure))
            await Assert.ThrowsAsync<InjectedFailure>(() => Checkout(context).CreateOrderAsync(products.UserId, request));
        Assert.True(failure.SqlSaveCompleted);
        await using (var verification = database.Context())
        {
            Assert.False(await verification.Orders.AnyAsync(order => order.UserId == products.UserId));
            Assert.False(await verification.OrderStatusHistories.AnyAsync(entry => entry.ChangedByUserId == products.UserId));
            Assert.False(await verification.OrderItems.AnyAsync(item => item.ProductId == products.FirstId || item.ProductId == products.SecondId));
            var remaining = await verification.CartItems.Where(item => item.Cart.UserId == products.UserId).OrderBy(item => item.ProductId).ToArrayAsync();
            Assert.Equal(new[] { products.FirstId, products.SecondId }, remaining.Select(item => item.ProductId));
            Assert.Equal(new[] { 3, 7 }, remaining.Select(item => item.Quantity));
            Assert.Equal(products.FirstStock, (await verification.Products.FindAsync(products.FirstId))!.Stock);
            Assert.Equal(products.SecondStock, (await verification.Products.FindAsync(products.SecondId))!.Stock);
        }

        OrderResponseDto completed;
        await using (var context = database.Context())
            completed = await Checkout(context).CreateOrderAsync(products.UserId, request);
        await using (var context = database.Context())
            Assert.Equal(completed.Id, (await Checkout(context).CreateOrderAsync(products.UserId, request)).Id);
        await using var final = database.Context();
        Assert.Equal(1, await final.Orders.CountAsync(order => order.UserId == products.UserId));
        Assert.Equal(2, await final.OrderItems.CountAsync(item => item.Order.UserId == products.UserId));
        Assert.Equal(1, await final.OrderStatusHistories.CountAsync(entry => entry.ChangedByUserId == products.UserId));
        Assert.False(await final.CartItems.AnyAsync(item => item.Cart.UserId == products.UserId));
        Assert.Equal(products.FirstStock - 3, (await final.Products.FindAsync(products.FirstId))!.Stock);
        Assert.Equal(products.SecondStock - 7, (await final.Products.FindAsync(products.SecondId))!.Stock);
        Assert.Equal(59.03m, completed.TotalAmount);
    }

    [SqlServerFact]
    public async Task RetryingCompletedRequest_DoesNotConsumeNewCartOrDeductInventoryAgain()
    {
        var products = await ProductsAsync();
        await using var factory = new SqlApiFactory(database, checkoutPolicy: new(Shipping));
        var customer = await factory.CustomerAsync();
        using var client = customer.Client;
        await AddAsync(client, products.FirstId, 2);
        var request = Request((await QuoteAsync(client)).Version);
        var order = await PlaceAsync(client, request);
        await AddAsync(client, products.SecondId, 4);
        var pending = await QuoteAsync(client);
        for (var retry = 0; retry < 2; retry++)
            Assert.Equal(order.Id, (await PlaceAsync(client, request)).Id);
        var afterReplay = await QuoteAsync(client);
        Assert.Equal(pending.Version, afterReplay.Version);
        Assert.Equal(products.SecondId, Assert.Single(afterReplay.Cart.Items).ProductId);
        Assert.Equal(4, afterReplay.Cart.TotalQuantity);
        Assert.Equal(1.16m, afterReplay.Cart.SubTotal);
        request.ShippingAddress!.FullName = "Changed request body";
        var mismatch = await client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Contains("IdempotencyMismatch", await mismatch.Content.ReadAsStringAsync());
        Assert.Equal(pending.Version, (await QuoteAsync(client)).Version);
        await using var verification = database.Context();
        Assert.Equal(1, await verification.Orders.CountAsync(value => value.UserId == customer.User.Id));
        Assert.Equal(products.FirstStock - 2, (await verification.Products.FindAsync(products.FirstId))!.Stock);
        Assert.Equal(products.SecondStock, (await verification.Products.FindAsync(products.SecondId))!.Stock);
    }

    [SqlServerFact]
    public async Task ConsumedGuestCookie_CannotRestorePurchasedItemsOrExposeAnotherCustomersCartAndOrder()
    {
        var products = await ProductsAsync();
        await using var factory = new SqlApiFactory(database, checkoutPolicy: new(Shipping));
        using var guest = factory.Client();
        var guestResponse = await guest.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = products.FirstId, Quantity = 2 });
        guestResponse.EnsureSuccessStatusCode();
        var guestCart = (await guestResponse.Content.ReadFromJsonAsync<CartResponseDto>())!;
        Assert.True(guestCart.IsGuest);
        var guestCookie = Assert.Single(guestResponse.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-TechStore.Cart=", StringComparison.Ordinal)).Split(';')[0];
        using var unrelatedGuest = factory.Client();
        await AddAsync(unrelatedGuest, products.SecondId, 5);
        var customer = await factory.CustomerAsync();
        using var client = customer.Client;
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", guestCookie);
        var merged = await client.PostAsJsonAsync("/api/cart/merge", new { });
        merged.EnsureSuccessStatusCode();
        Assert.Equal(2, (await merged.Content.ReadFromJsonAsync<CartResponseDto>())!.TotalQuantity);
        var request = Request((await QuoteAsync(client)).Version);
        var completed = await PlaceAsync(client, request);
        await AssertEmptyCartAsync(client, customer.User.Id);

        // Deliberately replay the already-consumed secret, including from another
        // authenticated owner. Neither a refresh nor a later merge may revive it.
        var replay = await client.PostAsJsonAsync("/api/cart/merge", new { });
        replay.EnsureSuccessStatusCode();
        Assert.Empty((await replay.Content.ReadFromJsonAsync<CartResponseDto>())!.Items);
        Assert.Empty((await guest.GetFromJsonAsync<CartResponseDto>("/api/cart"))!.Items);
        var other = await factory.CustomerAsync();
        using var otherClient = other.Client;
        otherClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", guestCookie);
        var foreignMerge = await otherClient.PostAsJsonAsync("/api/cart/merge", new { });
        foreignMerge.EnsureSuccessStatusCode();
        Assert.Empty((await foreignMerge.Content.ReadFromJsonAsync<CartResponseDto>())!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/orders/{completed.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/orders/by-request/{request.ClientRequestId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await otherClient.PostAsJsonAsync("/api/orders", request)).StatusCode);
        await AddAsync(client, products.SecondId, 3);
        var next = await QuoteAsync(client);
        Assert.Equal(products.SecondId, Assert.Single(next.Cart.Items).ProductId);
        Assert.Equal(3, next.Cart.TotalQuantity);
        var independent = (await unrelatedGuest.GetFromJsonAsync<CartResponseDto>("/api/cart"))!;
        Assert.Equal(products.SecondId, Assert.Single(independent.Items).ProductId);
        Assert.Equal(5, independent.TotalQuantity);
        await using var verification = database.Context();
        Assert.False(await verification.Carts.AnyAsync(cart => cart.Id == guestCart.Id));
        Assert.Equal(1, await verification.Orders.CountAsync(order => order.UserId == customer.User.Id));
        Assert.False(await verification.Orders.AnyAsync(order => order.UserId == other.User.Id));
        Assert.Equal(3, await verification.CartItems.Where(item => item.Cart.UserId == customer.User.Id).Select(item => item.Quantity).SingleAsync());
        Assert.False(await verification.CartItems.AnyAsync(item => item.Cart.UserId == other.User.Id));
        Assert.Equal(products.FirstStock - 2, (await verification.Products.FindAsync(products.FirstId))!.Stock);
        Assert.Equal(products.SecondStock, (await verification.Products.FindAsync(products.SecondId))!.Stock);
    }

    private async Task<Products> ProductsAsync()
    {
        var scenario = await database.SeedAsync(stock: 20, cart: false);
        await using var context = database.Context();
        var first = (await context.Products.FindAsync(scenario.ProductId))!;
        first.UpdateDetails(first.Name, first.Description, first.SKU, first.Brand, 19.99m, 17.35m, first.Stock, first.CategoryId, true);
        var second = Product.Create("Second SQL decimal product", "Isolated checkout regression", Guid.NewGuid().ToString("N"),
            "Test brand", 0.29m, null, 30, first.CategoryId, true);
        context.Products.Add(second);
        await context.SaveChangesAsync();
        return new(scenario.UserId, first.Id, second.Id, 20, 30);
    }

    private async Task AssertEmptyCartAsync(HttpClient client, string userId)
    {
        using var response = await client.GetAsync("/api/cart");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var cart = (await response.Content.ReadFromJsonAsync<CartResponseDto>())!;
        Assert.Empty(cart.Items);
        Assert.Equal(0, cart.TotalQuantity);
        Assert.Equal(0m, cart.SubTotal);
        Assert.Equal(0m, cart.OriginalSubTotal);
        Assert.Equal(0m, cart.DiscountTotal);
        Assert.False(cart.CanCheckout);
        var quote = await QuoteAsync(client);
        Assert.Empty(quote.Cart.Items);
        await using var verification = database.Context();
        Assert.False(await verification.CartItems.AnyAsync(item => item.Cart.UserId == userId));
    }

    private static async Task AddAsync(HttpClient client, int productId, int quantity = 1) =>
        (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto
        { ProductId = productId, Quantity = quantity, ClientRequestId = Guid.NewGuid() })).EnsureSuccessStatusCode();

    private static async Task<CheckoutQuoteDto> QuoteAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;

    private static async Task<OrderResponseDto> PlaceAsync(HttpClient client, CreateOrderDto request)
    {
        using var response = await client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<OrderResponseDto>())!;
    }

    private static CreateOrderDto Request(string version) => new()
    {
        ClientRequestId = Guid.NewGuid(), QuoteVersion = version,
        ShippingAddress = new() { FullName = "SQL purchase customer", Phone = "01012345678", City = "Cairo", Street = "Isolated purchase street", Building = "1" }
    };

    private static CartService CartService(TechStore.Infrastructure.Persistence.ApplicationDbContext context) =>
        new(new CommerceRepository(context), new AddToCartValidator(), new UpdateCartItemValidator());
    private static CheckoutService Checkout(TechStore.Infrastructure.Persistence.ApplicationDbContext context) =>
        new(new CommerceRepository(context), new CreateOrderValidator(), new CheckoutPolicy(Shipping));
    private sealed record Products(string UserId, int FirstId, int SecondId, int FirstStock, int SecondStock);
    private sealed class InjectedFailure : Exception;
    private sealed class FailAfterOrderSqlSave : SaveChangesInterceptor
    {
        public bool SqlSaveCompleted { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            SqlSaveCompleted = true;
            throw new InjectedFailure();
        }
    }
}
