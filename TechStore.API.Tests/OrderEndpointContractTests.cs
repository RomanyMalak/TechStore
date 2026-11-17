using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Services;
using TechStore.Domain.Enums;

namespace TechStore.API.Tests;

public class OrderEndpointContractTests(OrderEndpointContractTests.OrderFactory factory) : IClassFixture<OrderEndpointContractTests.OrderFactory>
{
    [Fact]
    public async Task CustomerList_BindsFiltersAndUsesJwtOwnershipRatherThanQueryUserId()
    {
        using var client = await factory.CustomerClientAsync();
        var userId = Subject(client);
        var response = await client.GetAsync("/api/orders?PageNumber=2&PageSize=5&Search=TS-123&Status=1"
            + "&FromDateUtc=2026-11-01T00%3A00%3A00Z&ToDateUtc=2026-11-08T00%3A00%3A00Z&UserId=someone-else");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var page = (await response.Content.ReadFromJsonAsync<PagedResult<OrderListDto>>())!;
        Assert.Equal(userId, Assert.Single(page.Items).UserId);
        factory.Orders.Verify(service => service.GetCustomerOrdersAsync(userId,
            It.Is<OrderQueryDto>(query => query.PageNumber == 2 && query.PageSize == 5 && query.Search == "TS-123"
                && query.Status == OrderStatus.Confirmed && query.FromDateUtc == new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
                && query.ToDateUtc == new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CustomerDetailsAndHistory_UseJwtOwnershipAndPrivateCaching()
    {
        using var client = await factory.CustomerClientAsync();
        var userId = Subject(client);
        var details = await client.GetAsync("/api/orders/17?UserId=someone-else");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.True(details.Headers.CacheControl?.NoStore);
        Assert.Equal(userId, (await details.Content.ReadFromJsonAsync<OrderResponseDto>())!.UserId);
        var history = await client.GetAsync("/api/orders/17/history?UserId=someone-else");
        Assert.True(history.Headers.CacheControl?.NoStore);
        Assert.Single((await history.Content.ReadFromJsonAsync<OrderStatusHistoryResponseDto[]>())!);
        factory.Orders.Verify(service => service.GetCustomerOrderAsync(userId, 17, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CustomerCancellation_DerivesActorFromJwtAndForwardsExpectedStatusAndNote()
    {
        using var client = await factory.CustomerClientAsync();
        var userId = Subject(client);
        var response = await client.PostAsJsonAsync("/api/orders/17/cancel", new
        {
            expectedStatus = OrderStatus.Pending, note = "Changed my mind", userId = "another-account"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(OrderStatus.Cancelled, (await response.Content.ReadFromJsonAsync<OrderResponseDto>())!.Status);
        factory.Orders.Verify(service => service.CancelCustomerOrderAsync(userId, 17,
            It.Is<CancelOrderDto>(dto => dto.ExpectedStatus == OrderStatus.Pending && dto.Note == "Changed my mind"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Admin_CanListReadAndGetHistoryForOrdersWithoutCustomerOwnershipRestriction()
    {
        using var client = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/orders?Search=TS-ADMIN&PageNumber=3&PageSize=7&Status=2")).StatusCode);
        var details = await client.GetAsync("/api/admin/orders/18");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.True(details.Headers.CacheControl?.NoStore);
        Assert.Equal("other-customer", (await details.Content.ReadFromJsonAsync<OrderResponseDto>())!.UserId);
        Assert.Single((await client.GetFromJsonAsync<OrderStatusHistoryResponseDto[]>("/api/admin/orders/18/history"))!);
        factory.Orders.Verify(service => service.GetAdminOrdersAsync(It.Is<OrderQueryDto>(query => query.Search == "TS-ADMIN"
            && query.PageNumber == 3 && query.PageSize == 7 && query.Status == OrderStatus.Processing), It.IsAny<CancellationToken>()), Times.Once);
        factory.Orders.Verify(service => service.GetAdminOrderAsync(18, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AdminStatusUpdate_DerivesResponsibleAdminFromJwt()
    {
        using var client = await factory.AdminClientAsync();
        var adminId = Subject(client);
        var response = await client.PatchAsJsonAsync("/api/admin/orders/18/status", new
        {
            status = OrderStatus.Confirmed, expectedStatus = OrderStatus.Pending, note = "Checked details", adminId = "forged-actor"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(OrderStatus.Confirmed, (await response.Content.ReadFromJsonAsync<OrderResponseDto>())!.Status);
        factory.Orders.Verify(service => service.UpdateStatusAsync(adminId, 18, It.Is<UpdateOrderStatusDto>(dto =>
            dto.Status == OrderStatus.Confirmed && dto.ExpectedStatus == OrderStatus.Pending && dto.Note == "Checked details"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":1}")]
    [InlineData("{\"expectedStatus\":0}")]
    [InlineData("{\"status\":99,\"expectedStatus\":0}")]
    [InlineData("{\"status\":1,\"expectedStatus\":99}")]
    public async Task AdminStatusUpdate_MissingOrInvalidStatusFields_Return400BeforeService(string json)
    {
        using var client = await factory.AdminClientAsync();
        var before = factory.Orders.Invocations.Count;
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PatchAsync("/api/admin/orders/18/status", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, factory.Orders.Invocations.Count);
    }

    [Fact]
    public async Task CustomerCancellation_MissingExpectedStatus_Return400BeforeService()
    {
        using var client = await factory.CustomerClientAsync();
        var before = factory.Orders.Invocations.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders/17/cancel", new { })).StatusCode);
        Assert.Equal(before, factory.Orders.Invocations.Count);
    }

    [Fact]
    public async Task CustomerAndGuest_AdminRequestsNeverInvokeOrderService()
    {
        using var customer = await factory.CustomerClientAsync();
        using var guest = factory.Client();
        var before = factory.Orders.Invocations.Count;
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/admin/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/admin/orders")).StatusCode);
        Assert.Equal(before, factory.Orders.Invocations.Count);
    }

    [Fact]
    public async Task ExpectedStateConflict_Return409ProblemDetails()
    {
        using var client = await factory.AdminClientAsync();
        var response = await client.PatchAsJsonAsync("/api/admin/orders/409/status", new
        { status = OrderStatus.Confirmed, expectedStatus = OrderStatus.Pending });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static string Subject(HttpClient client) =>
        new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter).Subject;

    public sealed class OrderFactory : ApiFactory
    {
        public Mock<IOrderService> Orders { get; } = new(MockBehavior.Strict);

        public OrderFactory()
        {
            Orders.Setup(service => service.GetCustomerOrdersAsync(It.IsAny<string>(), It.IsAny<OrderQueryDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string userId, OrderQueryDto query, CancellationToken _) => Page(userId, query));
            Orders.Setup(service => service.GetCustomerOrderAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string userId, int id, CancellationToken _) => Order(userId, id));
            Orders.Setup(service => service.CancelCustomerOrderAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancelOrderDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string userId, int id, CancelOrderDto _, CancellationToken _) => Order(userId, id, OrderStatus.Cancelled));
            Orders.Setup(service => service.GetAdminOrdersAsync(It.IsAny<OrderQueryDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((OrderQueryDto query, CancellationToken _) => Page("other-customer", query));
            Orders.Setup(service => service.GetAdminOrderAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, CancellationToken _) => Order("other-customer", id));
            Orders.Setup(service => service.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateOrderStatusDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, int id, UpdateOrderStatusDto dto, CancellationToken _) => Order("other-customer", id, dto.Status!.Value));
            Orders.Setup(service => service.UpdateStatusAsync(It.IsAny<string>(), 409, It.IsAny<UpdateOrderStatusDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConflictException("The order changed concurrently."));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrderService>();
                services.AddScoped(_ => Orders.Object);
            });
        }

        private static PagedResult<OrderListDto> Page(string userId, OrderQueryDto query) =>
            new(new[] { new OrderListDto { Id = 17, UserId = userId, OrderNumber = "TS-TEST", Status = OrderStatus.Pending } },
                1, query.PageNumber, query.PageSize);
        private static OrderResponseDto Order(string userId, int id, OrderStatus status = OrderStatus.Pending) => new()
        {
            Id = id, UserId = userId, OrderNumber = "TS-TEST", Status = status,
            StatusHistory = [new OrderStatusHistoryResponseDto { Id = 1, NewStatus = OrderStatus.Pending }]
        };
    }
}
