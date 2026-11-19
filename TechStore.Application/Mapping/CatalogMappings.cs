using TechStore.Application.DTOs.Categories;
using TechStore.Application.DTOs.Products;
using TechStore.Domain.Entities;

namespace TechStore.Application.Mapping;

internal static class CatalogMappings
{
    public static ProductResponseDto ToResponse(this Product product) => product.ToResponse(product.Category.Name);

    public static ProductResponseDto ToResponse(this Product product, string categoryName) => new()
    {
        Id = product.Id, Name = product.Name, Description = product.Description,
        SKU = product.SKU, Brand = product.Brand, Price = product.Price,
        DiscountPrice = product.DiscountPrice, Stock = product.Stock,
        CategoryId = product.CategoryId, CategoryName = categoryName,
        IsActive = product.IsActive, CreatedAt = product.CreatedAt, UpdatedAt = product.UpdatedAt,
        RowVersion = product.RowVersion.ToArray(),
        Images = product.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id)
            .Select(image => new ProductImageResponseDto
            {
                Id = image.Id, ImageUrl = image.ImageUrl, IsPrimary = image.IsPrimary, SortOrder = image.SortOrder
            }).ToList()
    };

    public static ProductListDto ToListItem(this Product product) => new()
    {
        Id = product.Id, Name = product.Name, SKU = product.SKU, Brand = product.Brand,
        Price = product.Price, DiscountPrice = product.DiscountPrice, Stock = product.Stock,
        CategoryId = product.CategoryId, CategoryName = product.Category.Name, IsActive = product.IsActive,
        PrimaryImageUrl = product.Images.FirstOrDefault(image => image.IsPrimary)?.ImageUrl
    };

    public static CategoryResponseDto ToResponse(this Category category) => category.ToResponse(category.ParentCategory?.Name);

    public static CategoryResponseDto ToResponse(this Category category, string? parentName) => new()
    {
        Id = category.Id, Name = category.Name, Description = category.Description,
        ParentCategoryId = category.ParentCategoryId, ParentCategoryName = parentName,
        IsActive = category.IsActive, CreatedAt = category.CreatedAt
    };
}
