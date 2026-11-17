using System.Net.Http.Json;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.DTOs.Products;

namespace TechStore.API.Tests;

internal static class CatalogRequests
{
    public static HttpRequestMessage Management(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "DELETE")
        {
            if (route.StartsWith("/api/products", StringComparison.Ordinal))
                request.Headers.TryAddWithoutValidation("If-Match", '"' + Convert.ToBase64String(ApiFactory.Version) + '"');
        }
        else if (route.EndsWith("/stock", StringComparison.Ordinal))
            request.Content = JsonContent.Create(new UpdateStockDto { QuantityChange = 1, RowVersion = ApiFactory.Version });
        else if (route.StartsWith("/api/products", StringComparison.Ordinal))
            request.Content = method == "PUT" ? JsonContent.Create(Update()) : JsonContent.Create(Create());
        else
            request.Content = method == "PUT" ? JsonContent.Create(new UpdateCategoryDto { Name = "Electronics" })
                : JsonContent.Create(new CreateCategoryDto { Name = "Electronics" });
        return request;
    }

    public static CreateProductDto Create() => new()
    {
        Name = "Laptop", Description = "Description", SKU = "SKU-1", Brand = "Brand", Price = 100, Stock = 10, CategoryId = 1
    };

    public static UpdateProductDto Update() => new()
    {
        Name = "Laptop", Description = "Description", SKU = "SKU-1", Brand = "Brand", Price = 100, Stock = 10,
        CategoryId = 1, RowVersion = ApiFactory.Version
    };
}
