using System.Net;
using System.Net.Http.Json;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using Microsoft.EntityFrameworkCore;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class CheckoutRecoverySqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task RecoveryLookup_IsOwnerScoped_AndRecoversTheSameCommittedOrderWithoutResubmitting()
    {
        var scenario=await database.SeedAsync(cart:false);
        await using var factory=new SqlApiFactory(database);
        var customer=await factory.CustomerAsync();using var client=customer.Client;
        var other=await factory.CustomerAsync();using var otherClient=other.Client;
        using var guest=factory.Client();
        (await client.PostAsJsonAsync("/api/cart/items",new AddToCartDto{ProductId=scenario.ProductId})).EnsureSuccessStatusCode();
        var quote=(await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;
        var requestId=Guid.NewGuid();
        var created=await client.PostAsJsonAsync("/api/orders",new CreateOrderDto{ClientRequestId=requestId,QuoteVersion=quote.Version,ShippingAddress=new(){FullName="Recovery customer",Phone="01012345678",City="Cairo",Street="Isolated recovery street",Building="1"}});
        created.EnsureSuccessStatusCode();var order=(await created.Content.ReadFromJsonAsync<OrderResponseDto>())!;
        var path=$"/api/orders/by-request/{requestId}";
        Assert.Equal(HttpStatusCode.Unauthorized,(await guest.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await otherClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/orders/by-request/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(order.Id,(await client.GetFromJsonAsync<OrderResponseDto>(path))!.Id);
        Assert.Equal(order.Id,(await client.GetFromJsonAsync<OrderResponseDto>(path))!.Id);
        await using var verify=database.Context();Assert.Equal(scenario.InitialStock-1,(await verify.Products.FindAsync(scenario.ProductId))!.Stock);
        Assert.Equal(1,await verify.Orders.CountAsync(item=>item.UserId==customer.User.Id));
    }
}
