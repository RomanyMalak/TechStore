using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TechStore.Domain.Enums;

namespace TechStore.API.Tests;

public class OrderAuthorizationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static IEnumerable<object[]> AdminOperations =>
    [
        ["GET", "/api/admin/orders"],
        ["GET", "/api/admin/orders/1"],
        ["GET", "/api/admin/orders/1/history"],
        ["PATCH", "/api/admin/orders/1/status"]
    ];

    public static IEnumerable<object[]> CustomerOperations =>
    [
        ["GET", "/api/orders"],
        ["GET", "/api/orders/1"],
        ["GET", "/api/orders/1/history"],
        ["POST", "/api/orders/1/cancel"]
    ];

    [Theory]
    [MemberData(nameof(AdminOperations))]
    [MemberData(nameof(CustomerOperations))]
    public async Task Guest_CannotAccessOrderOperations(string method, string route)
    {
        using var client = factory.Client();
        using var request = Request(method, route);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
    }

    [Theory]
    [MemberData(nameof(AdminOperations))]
    public async Task Customer_CannotAccessAdminOrderOperations(string method, string route)
    {
        using var client = await factory.CustomerClientAsync();
        using var request = Request(method, route);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Customer_MissingOrderAndHistoryOrCancellation_Return404()
    {
        using var client = await factory.CustomerClientAsync();
        // This factory has no orders, so ownership-aware lookups must not disclose any order.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/orders/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/orders/1/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/orders/1/cancel", new
        { expectedStatus = OrderStatus.Pending, note = "No longer needed" })).StatusCode);
    }

    [Fact]
    public async Task Swagger_DescribesProtectedCustomerAndAdminOrderOperations()
    {
        using var client = factory.Client();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var route in new[] { "/api/orders", "/api/orders/{id}/history", "/api/admin/orders", "/api/admin/orders/{id}", "/api/admin/orders/{id}/history" })
        {
            var operation = paths.GetProperty(route).GetProperty("get");
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        }
        foreach (var operation in new[]
        {
            paths.GetProperty("/api/orders/{id}/cancel").GetProperty("post"),
            paths.GetProperty("/api/admin/orders/{id}/status").GetProperty("patch")
        })
        {
            var responses = operation.GetProperty("responses");
            Assert.True(responses.TryGetProperty("200", out _));
            Assert.True(responses.TryGetProperty("409", out _));
            Assert.True(responses.TryGetProperty("429", out _));
        }
    }

    private static HttpRequestMessage Request(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PATCH") request.Content = JsonContent.Create(new
        {
            status = OrderStatus.Confirmed, expectedStatus = OrderStatus.Pending, note = "Verified test transition"
        });
        return request;
    }
}
