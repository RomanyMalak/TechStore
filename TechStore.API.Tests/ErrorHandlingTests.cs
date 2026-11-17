using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TechStore.API.Tests;

public class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task InvalidDto_Returns400WithFieldErrorsAndTraceId()
    {
        using var client = await factory.AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/products", new { });
        using var document = await Problem(response, HttpStatusCode.BadRequest);
        Assert.True(document.RootElement.GetProperty("errors").EnumerateObject().Any());
    }

    [Fact]
    public async Task ApplicationValidation_Returns400WithErrors()
    {
        using var client = factory.Client();
        using var document = await Problem(await client.GetAsync("/api/products?PageSize=0"), HttpStatusCode.BadRequest);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("PageSize", out _));
    }

    [Fact]
    public async Task MissingProduct_Returns404()
    {
        using var client = factory.Client();
        using var document = await Problem(await client.GetAsync("/api/products/404"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DuplicateSku_Returns409()
    {
        using var client = await factory.AdminClientAsync();
        var dto = CatalogRequests.Create(); dto.SKU = "DUPLICATE";
        using var document = await Problem(await client.PostAsJsonAsync("/api/products", dto), HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(404, HttpStatusCode.NotFound)]
    public async Task InvalidOrMissingCategory_IsMappedCorrectly(int categoryId, HttpStatusCode expected)
    {
        using var client = await factory.AdminClientAsync();
        var dto = CatalogRequests.Create(); dto.CategoryId = categoryId;
        using var document = await Problem(await client.PostAsJsonAsync("/api/products", dto), expected);
    }

    [Fact]
    public async Task InvalidRowVersion_Returns400()
    {
        using var client = await factory.AdminClientAsync();
        var dto = CatalogRequests.Update(); dto.RowVersion = new byte[2];
        using var document = await Problem(await client.PutAsJsonAsync("/api/products/1", dto), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-base64")]
    [InlineData("*")]
    [InlineData("W/\"AAAAAAAAAAE=\"")]
    public async Task Delete_RequiresStrongValidRowVersion(string? ifMatch)
    {
        using var client = await factory.AdminClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/products/1");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var document = await Problem(await client.SendAsync(request), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("STALE")]
    [InlineData("EF-CONCURRENCY")]
    public async Task ApplicationOrEfConcurrency_Returns409(string sku)
    {
        using var client = await factory.AdminClientAsync();
        var dto = CatalogRequests.Update(); dto.SKU = sku;
        using var document = await Problem(await client.PutAsJsonAsync("/api/products/1", dto), HttpStatusCode.Conflict);
        Assert.DoesNotContain("Internal EF details", document.RootElement.ToString());
    }

    [Fact]
    public async Task UnexpectedException_DoesNotExposeInternalDetails()
    {
        using var client = factory.Client();
        using var document = await Problem(await client.GetAsync("/api/products/500"), HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("Sensitive internal", document.RootElement.ToString());
        Assert.DoesNotContain("stackTrace", document.RootElement.ToString());
    }

    private static async Task<JsonDocument> Problem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, document.RootElement.GetProperty("status").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
        return document;
    }
}
