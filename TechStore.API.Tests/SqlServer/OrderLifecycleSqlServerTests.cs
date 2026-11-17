using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Services;
using TechStore.Application.Validators.Commerce;
using TechStore.Application.Validators.Orders;
using TechStore.Domain.Entities;
using TechStore.Domain.Enums;
using TechStore.Infrastructure.Repositories;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class OrderLifecycleSqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task RealSqlApis_EnforceOwnershipAdminWorkflowAndCancellationAudit()
    {
        var scenario = await database.SeedAsync(cart: false);
        await using var factory = new SqlApiFactory(database);
        var customer = await factory.CustomerAsync(); using var client = customer.Client;
        var other = await factory.CustomerAsync(); using var otherClient = other.Client;
        var admin = await factory.AdminAsync(); using var adminClient = admin.Client;
        using var guest = factory.Client();
        var order = await CreateOrderAsync(client, scenario.ProductId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/orders/{order.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsJsonAsync($"/api/orders/{order.Id}/cancel", new CancelOrderDto { ExpectedStatus = OrderStatus.Pending })).StatusCode);
        var own = (await client.GetFromJsonAsync<PagedResult<OrderListDto>>("/api/orders?PageSize=1&UserId=" + other.User.Id))!;
        Assert.Equal(1, own.TotalCount); Assert.Equal(order.Id, Assert.Single(own.Items).Id);
        using (var json = JsonDocument.Parse(await client.GetStringAsync($"/api/orders/{order.Id}")))
        {
            Assert.EndsWith("Z", json.RootElement.GetProperty("orderDate").GetString());
            Assert.EndsWith("Z", json.RootElement.GetProperty("statusHistory")[0].GetProperty("changedAt").GetString());
        }
        var listed = (await adminClient.GetFromJsonAsync<PagedResult<OrderListDto>>("/api/admin/orders?Search=" + order.OrderNumber))!;
        Assert.Equal(order.Id, Assert.Single(listed.Items).Id);
        var route = $"/api/admin/orders/{order.Id}/status";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync(route, new UpdateOrderStatusDto { Status = OrderStatus.Confirmed, ExpectedStatus = OrderStatus.Pending })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PatchAsJsonAsync(route, new UpdateOrderStatusDto { Status = OrderStatus.Confirmed, ExpectedStatus = OrderStatus.Pending })).StatusCode);
        (await adminClient.PatchAsJsonAsync(route, new UpdateOrderStatusDto { Status = OrderStatus.Confirmed, ExpectedStatus = OrderStatus.Pending, Note = "Confirmed in isolated SQL test" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PatchAsJsonAsync(route, new UpdateOrderStatusDto { Status = OrderStatus.Delivered, ExpectedStatus = OrderStatus.Confirmed })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PatchAsJsonAsync(route, new UpdateOrderStatusDto { Status = OrderStatus.Processing, ExpectedStatus = OrderStatus.Pending })).StatusCode);
        var cancel = new CancelOrderDto { ExpectedStatus = OrderStatus.Confirmed, Note = "Customer cancellation" };
        var cancelled = await client.PostAsJsonAsync($"/api/orders/{order.Id}/cancel", cancel);
        cancelled.EnsureSuccessStatusCode();
        Assert.Equal(OrderStatus.Cancelled, (await cancelled.Content.ReadFromJsonAsync<OrderResponseDto>())!.Status);
        (await client.PostAsJsonAsync($"/api/orders/{order.Id}/cancel", cancel)).EnsureSuccessStatusCode();
        var history = (await client.GetFromJsonAsync<List<OrderStatusHistoryResponseDto>>($"/api/orders/{order.Id}/history"))!;
        Assert.Equal(3, history.Count);
        Assert.Equal(admin.User.Id, history.Single(entry => entry.NewStatus == OrderStatus.Confirmed).ChangedByUserId);
        Assert.Equal(customer.User.Id, history.Single(entry => entry.NewStatus == OrderStatus.Cancelled).ChangedByUserId);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"/api/admin/orders/{order.Id}")).StatusCode);
        await using var verify = database.Context();
        Assert.Equal(scenario.InitialStock, (await verify.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Equal(OrderStatus.Cancelled, (await verify.Orders.FindAsync(order.Id))!.Status);
    }

    [SqlServerFact]
    public async Task ConcurrentCancellation_RestoresExactlyOnce_WithIdempotentRetry()
    {
        var scenario = await database.SeedAsync();
        var order = await PlaceDirectAsync(scenario.UserId);
        var gate = new CommerceSqlServerTests.ReadGate();
        async Task<bool> Cancel(CommerceSqlServerTests.ReadGate? synchronization)
        {
            await using var context = database.Context();
            try
            {
                await Service(new GatedOrders(new OrderRepository(context), synchronization)).CancelCustomerOrderAsync(scenario.UserId, order.Id,
                    new CancelOrderDto { ExpectedStatus = OrderStatus.Pending });
                return true;
            }
            catch (ConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Cancel(gate), Cancel(gate));
        Assert.Single(outcomes, success => success);
        Assert.True(await Cancel(null));
        Assert.True(await Cancel(null));
        await using var verify = database.Context();
        Assert.Equal(scenario.InitialStock, (await verify.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Equal(OrderStatus.Cancelled, (await verify.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(1, await verify.OrderStatusHistories.CountAsync(entry => entry.OrderId == order.Id && entry.NewStatus == OrderStatus.Cancelled));
    }

    [SqlServerFact]
    public async Task CancellationFailureAfterSqlSave_RollsBackStatusHistoryAndRestoredStock()
    {
        var scenario = await database.SeedAsync();
        var order = await PlaceDirectAsync(scenario.UserId);
        var failure = new FailAfterSave();
        await using (var context = database.Context(failure))
            await Assert.ThrowsAsync<InjectedFailure>(() => Service(new OrderRepository(context)).CancelCustomerOrderAsync(scenario.UserId, order.Id,
                new CancelOrderDto { ExpectedStatus = OrderStatus.Pending }));
        Assert.True(failure.SqlSaveCompleted);
        await using var verify = database.Context();
        Assert.Equal(scenario.InitialStock - 1, (await verify.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Equal(OrderStatus.Pending, (await verify.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(1, await verify.OrderStatusHistories.CountAsync(entry => entry.OrderId == order.Id));
    }

    [SqlServerFact]
    public async Task CancellingOrderOfSoftDeletedProduct_RestoresStockWithoutRepublishingIt()
    {
        var scenario = await database.SeedAsync();
        var order = await PlaceDirectAsync(scenario.UserId);
        await using (var edit = database.Context())
        {
            (await edit.Products.FindAsync(scenario.ProductId))!.SoftDelete();
            await edit.SaveChangesAsync();
        }
        await using (var cancel = database.Context())
            await Service(new OrderRepository(cancel)).CancelCustomerOrderAsync(scenario.UserId, order.Id, new CancelOrderDto { ExpectedStatus = OrderStatus.Pending });
        await using var verify = database.Context();
        var product = (await verify.Products.FindAsync(scenario.ProductId))!;
        Assert.Equal(scenario.InitialStock, product.Stock);
        Assert.True(product.IsDeleted); Assert.False(product.IsActive);
    }

    private async Task<OrderResponseDto> PlaceDirectAsync(string userId)
    {
        await using var context = database.Context();
        var service = new CheckoutService(new CommerceRepository(context), new CreateOrderValidator(), new CheckoutPolicy());
        var quote = await service.GetQuoteAsync(userId);
        return await service.CreateOrderAsync(userId, new CreateOrderDto { ClientRequestId = Guid.NewGuid(), QuoteVersion = quote.Version,
            ShippingAddress = new() { FullName = "SQL customer", Phone = "01012345678", City = "Cairo", Street = "SQL test street", Building = "1" } });
    }

    private static async Task<OrderResponseDto> CreateOrderAsync(HttpClient client, int productId)
    {
        (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = productId, Quantity = 1 })).EnsureSuccessStatusCode();
        var quote = (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;
        var response = await client.PostAsJsonAsync("/api/orders", new CreateOrderDto { ClientRequestId = Guid.NewGuid(), QuoteVersion = quote.Version,
            ShippingAddress = new() { FullName = "SQL recipient", Phone = "01012345678", City = "Cairo", Street = "SQL test street", Building = "1" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponseDto>())!;
    }

    private static OrderService Service(IOrderRepository repository) => new(repository, new OrderQueryDtoValidator(), new UpdateOrderStatusDtoValidator(), new CancelOrderDtoValidator());

    private sealed class GatedOrders(IOrderRepository inner, CommerceSqlServerTests.ReadGate? gate) : IOrderRepository
    {
        public Task<PagedResult<OrderListDto>> GetAllAsync(OrderQueryDto query, string? userId, CancellationToken token) => inner.GetAllAsync(query, userId, token);
        public Task<Order?> GetByIdAsync(int id, string? userId, CancellationToken token) => inner.GetByIdAsync(id, userId, token);
        public async Task<Order?> GetForUpdateAsync(int id, string? userId, CancellationToken token)
        {
            var order = await inner.GetForUpdateAsync(id, userId, token);
            if (gate is not null) await gate.ArriveAsync(token);
            return order;
        }
        public Task<IReadOnlyList<Product>> GetProductsForUpdateAsync(IReadOnlyCollection<int> ids, CancellationToken token) => inner.GetProductsForUpdateAsync(ids, token);
        public Task<int> SaveChangesAsync(CancellationToken token) => inner.SaveChangesAsync(token);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) => inner.ExecuteInTransactionAsync(action, token);
    }

    private sealed class InjectedFailure : Exception;
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool SqlSaveCompleted { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        { SqlSaveCompleted = true; throw new InjectedFailure(); }
    }
}
