using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Domain.Entities;
using TechStore.Domain.Enums;
using TechStore.Infrastructure.Persistence;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests;

public class CommerceIntegrationTests
{
    [Fact]
    public async Task GuestToCustomerCheckout_PersistsOrderSnapshots_IsolatesOwners_AndDeductsStockOnce()
    {
        await using var factory = new CommerceFactory();
        using var client = factory.Client();
        var (productId, initialStock) = await Product(factory);
        var response = await client.GetAsync("/api/cart");
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.Contains("httponly", StringComparison.OrdinalIgnoreCase) && value.Contains("secure", StringComparison.OrdinalIgnoreCase));
        var cart = await (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = productId, Quantity = 2 })).Content.ReadFromJsonAsync<CartResponseDto>();
        Assert.True(cart!.IsGuest); Assert.Equal(2, cart.TotalQuantity); Assert.True(cart.CanCheckout);
        using var otherGuest = factory.Client();
        Assert.Empty((await otherGuest.GetFromJsonAsync<CartResponseDto>("/api/cart"))!.Items);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/checkout")).StatusCode);
        await SignIn(client);
        var merged = await (await client.PostAsJsonAsync("/api/cart/merge", new { })).Content.ReadFromJsonAsync<CartResponseDto>();
        Assert.False(merged!.IsGuest); Assert.Equal(2, merged.TotalQuantity);
        Assert.Equal(2, (await (await client.PostAsJsonAsync("/api/cart/merge", new { })).Content.ReadFromJsonAsync<CartResponseDto>())!.TotalQuantity);
        var quote = (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;
        Assert.Single(quote.PaymentMethods); Assert.Equal(PaymentMethod.CashOnDelivery, quote.PaymentMethods[0].Value);
        var request = Request(quote.Version);
        var created = await client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        var order = (await created.Content.ReadFromJsonAsync<OrderResponseDto>())!;
        Assert.Equal(quote.TotalAmount, order.TotalAmount); Assert.StartsWith("TS-", order.OrderNumber);
        Assert.Equal(2, order.Items.Single().Quantity); Assert.Contains("Cairo", order.ShippingAddress);
        Assert.Empty((await client.GetFromJsonAsync<CartResponseDto>("/api/cart"))!.Items);
        var replay = (await (await client.PostAsJsonAsync("/api/orders", request)).Content.ReadFromJsonAsync<OrderResponseDto>())!;
        Assert.Equal(order.Id, replay.Id);
        using var otherCustomer = await factory.CustomerClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.GetAsync($"/api/orders/{order.Id}")).StatusCode);
        await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = productId });
        await client.PostAsJsonAsync("/api/orders", request);
        Assert.Single((await client.GetFromJsonAsync<CartResponseDto>("/api/cart"))!.Items);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(initialStock - 2, (await context.Products.FindAsync(productId))!.Stock);
        Assert.Equal(1, await context.Orders.CountAsync());
    }
    [Fact]
    public async Task InvalidQuantities_PriceChanges_MockPayment_AndStockLoss_NeverCreateOrder()
    {
        await using var factory = new CommerceFactory(); using var client = factory.Client();
        var (id, _) = await Product(factory); await SignIn(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = id, Quantity = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = id, Quantity = int.MaxValue })).StatusCode);
        await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = id });
        var quote = (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;
        var request = Request(quote.Version); request.PaymentMethod = PaymentMethod.MockPayment;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", request)).StatusCode);
        request.PaymentMethod = PaymentMethod.CashOnDelivery;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); var product = (await context.Products.FindAsync(id))!;
            product.UpdateDetails(product.Name, product.Description, product.SKU, product.Brand, product.Price + 100, product.DiscountPrice, product.Stock, product.CategoryId, true);
            await context.SaveChangesAsync();
        }
        var conflict = await client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Contains("CartChanged", await conflict.Content.ReadAsStringAsync());
        request.QuoteVersion = (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!.Version;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); var product = (await context.Products.FindAsync(id))!;
            product.AdjustStock(-product.Stock); await context.SaveChangesAsync();
        }
        conflict = await client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Contains("StockUnavailable", await conflict.Content.ReadAsStringAsync());
        Assert.NotEmpty((await client.GetFromJsonAsync<CartResponseDto>("/api/cart"))!.Items);
        await using var finalScope = factory.Services.CreateAsyncScope();
        Assert.Empty(await finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Orders.ToListAsync());
    }
    private static CreateOrderDto Request(string version) => new()
    {
        ClientRequestId = Guid.NewGuid(), QuoteVersion = version,
        ShippingAddress = new() { FullName = "Integration Customer", Phone = "01012345678", City = "Cairo", Street = "Test street", Building = "10" }
    };
    private static async Task SignIn(HttpClient client)
    {
        var password = Guid.NewGuid().ToString("N") + "Aa1!"; var email = Guid.NewGuid().ToString("N") + "@example.test";
        (await client.PostAsJsonAsync("/api/auth/register", new RegisterDto { FullName = "Customer", Email = email, Password = password, ConfirmPassword = password })).EnsureSuccessStatusCode();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password })).Content.ReadFromJsonAsync<AuthResponseDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
    }
    private static async Task<(int Id, int Stock)> Product(CommerceFactory factory)
    {
        // Initializing the factory runs the existing Development seeder in its isolated in-memory DB.
        await using var scope = factory.Services.CreateAsyncScope();
        var product = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Products.SingleAsync(product => product.SKU == "LOG-MOU-001");
        return (product.Id, product.Stock);
    }
    private sealed class CommerceFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => { services.RemoveAll<ICommerceRepository>(); services.AddScoped<ICommerceRepository, NonTransactionalTestRepository>(); });
        }
    }
    // EF InMemory has no relational transactions. This adapter reuses production queries, while
    // transaction/locking guarantees are verified separately against real SQL Server.
    private sealed class NonTransactionalTestRepository(ApplicationDbContext context) : ICommerceRepository
    {
        private readonly CommerceRepository inner = new(context);
        public Task<Cart?> GetCartAsync(CartOwner owner, bool tracked, CancellationToken token) => inner.GetCartAsync(owner, tracked, token);
        public Task<Product?> GetProductAsync(int id, CancellationToken token) => inner.GetProductAsync(id, token);
        public Task<Address?> GetAddressAsync(string user, int id, CancellationToken token) => inner.GetAddressAsync(user, id, token);
        public Task<Order?> GetOrderAsync(string user, int id, CancellationToken token) => inner.GetOrderAsync(user, id, token);
        public Task<Order?> GetOrderByRequestAsync(string user, Guid id, CancellationToken token) => inner.GetOrderByRequestAsync(user, id, token);
        public Task AddCartAsync(Cart cart, CancellationToken token) => inner.AddCartAsync(cart, token);
        public Task<CartAdditionEvent?> GetCartAdditionByRequestAsync(string ownerHash, Guid request, CancellationToken token) => inner.GetCartAdditionByRequestAsync(ownerHash, request, token);
        public Task AddCartAdditionAsync(CartAdditionEvent addition, CancellationToken token) => inner.AddCartAdditionAsync(addition, token);
        public void RemoveCart(Cart cart) => inner.RemoveCart(cart);
        public Task AddOrderAsync(Order order, CancellationToken token) => inner.AddOrderAsync(order, token);
        public Task<int> SaveChangesAsync(CancellationToken token) => inner.SaveChangesAsync(token);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) => action(token);
    }
}
