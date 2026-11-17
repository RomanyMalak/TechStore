using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TechStore.Application.DTOs.Products;

namespace TechStore.API.Tests;

public class AuthorizationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static IEnumerable<object[]> ManagementOperations => new[]
    {
        new object[] { "POST", "/api/products", HttpStatusCode.Created },
        new object[] { "PUT", "/api/products/1", HttpStatusCode.OK },
        new object[] { "DELETE", "/api/products/1", HttpStatusCode.NoContent },
        new object[] { "PATCH", "/api/products/1/stock", HttpStatusCode.OK },
        new object[] { "POST", "/api/categories", HttpStatusCode.Created },
        new object[] { "PUT", "/api/categories/1", HttpStatusCode.OK },
        new object[] { "DELETE", "/api/categories/1", HttpStatusCode.NoContent }
    };

    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/products/1")]
    [InlineData("/api/categories")]
    [InlineData("/api/categories/1")]
    public async Task Guest_CanBrowse(string route)
    {
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(ManagementOperations))]
    public async Task Guest_ManagementIs401_WithoutCallingService(string method, string route, HttpStatusCode _)
    {
        using var client = factory.Client();
        var callsBefore = factory.Products.Invocations.Count + factory.Categories.Invocations.Count;
        using var request = CatalogRequests.Management(method, route);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(callsBefore, factory.Products.Invocations.Count + factory.Categories.Invocations.Count);
    }

    [Theory]
    [MemberData(nameof(ManagementOperations))]
    public async Task Customer_ManagementIs403_WithoutCallingService(string method, string route, HttpStatusCode _)
    {
        using var client = await factory.CustomerClientAsync();
        var callsBefore = factory.Products.Invocations.Count + factory.Categories.Invocations.Count;
        using var request = CatalogRequests.Management(method, route);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(callsBefore, factory.Products.Invocations.Count + factory.Categories.Invocations.Count);
    }

    [Theory]
    [MemberData(nameof(ManagementOperations))]
    public async Task Admin_ManagementSucceeds(string method, string route, HttpStatusCode expected)
    {
        using var client = await factory.AdminClientAsync();
        using var request = CatalogRequests.Management(method, route);
        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Customer_CannotEnableInactiveProductsByQueryParameter()
    {
        using var client = await factory.CustomerClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products?includeInactive=true")).StatusCode);
        var invocation = factory.Products.Invocations.Last(call => call.Method.Name == "GetProductsAsync");
        Assert.False((bool)invocation.Arguments[1]);
    }

    [Fact]
    public async Task Admin_BrowsingPassesServerDerivedVisibility()
    {
        using var client = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
        var invocation = factory.Products.Invocations.Last(call => call.Method.Name == "GetProductsAsync");
        Assert.True((bool)invocation.Arguments[1]);
    }

    [Fact]
    public async Task Create_ReturnsLocationAndETag()
    {
        using var client = await factory.AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/products", CatalogRequests.Create());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith("/api/products/1", response.Headers.Location?.ToString());
        Assert.Equal('"' + Convert.ToBase64String(ApiFactory.Version) + '"', response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task QueryDto_IsBoundAndForwardedToTheService()
    {
        using var client = factory.Client();
        var response = await client.GetAsync("/api/products?PageNumber=2&PageSize=5&Search=laptop&CategoryId=3&Brand=Dell&MinPrice=10&MaxPrice=100&InStockOnly=true&SortBy=PriceDescending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = (ProductQueryDto)factory.Products.Invocations.Last(call => call.Method.Name == "GetProductsAsync").Arguments[0];
        Assert.Equal(2, query.PageNumber);
        Assert.Equal(5, query.PageSize);
        Assert.Equal("laptop", query.Search);
        Assert.Equal(3, query.CategoryId);
        Assert.Equal("Dell", query.Brand);
        Assert.Equal(10, query.MinPrice);
        Assert.Equal(100, query.MaxPrice);
        Assert.True(query.InStockOnly);
        Assert.Equal(ProductSort.PriceDescending, query.SortBy);
    }

    [Fact]
    public async Task Swagger_DescribesBearerAndRequiredAdminSecurity()
    {
        using var client = factory.Client();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = document.RootElement;
        Assert.Equal("bearer", root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        var products = root.GetProperty("paths").GetProperty("/api/products");
        Assert.True(products.GetProperty("post").GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.Empty(products.GetProperty("get").GetProperty("security")[0].EnumerateObject());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }
}
