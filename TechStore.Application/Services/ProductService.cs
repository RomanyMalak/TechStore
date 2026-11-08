using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Interfaces.Storage;
using TechStore.Application.Mapping;
using TechStore.Domain.Entities;
using ValidationException = TechStore.Application.Exceptions.ValidationException;

namespace TechStore.Application.Services;

public sealed class ProductService(
    IProductRepository products,
    ICategoryRepository categories,
    IValidator<CreateProductDto> createValidator,
    IValidator<UpdateProductDto> updateValidator,
    IValidator<ProductQueryDto> queryValidator,
    IValidator<UpdateStockDto> stockValidator,
    IProductImageStore imageStore) : IProductService
{
    public async Task<ProductResponseDto> CreateProductAsync(CreateProductDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(createValidator, dto, cancellationToken);
        var sku = dto.SKU.Trim();
        if (await products.ExistsBySkuAsync(sku, cancellationToken: cancellationToken))
            throw new ConflictException($"SKU '{sku}' already exists.");
        var category = await RequireCategoryAsync(dto.CategoryId, cancellationToken);
        var product = RequestValidation.ApplyDomainRules(() => Product.Create(dto.Name, dto.Description,
            sku, dto.Brand, dto.Price, dto.DiscountPrice, dto.Stock, dto.CategoryId, dto.IsActive));
        ReplaceImages(product, dto.Images);
        await using var imageLease = await imageStore.AcquireAsync([], product.Images.Select(image => image.ImageUrl), cancellationToken);
        await products.AddAsync(product, cancellationToken);
        await products.SaveChangesAsync(cancellationToken);
        await imageLease.CommitAsync(CancellationToken.None);
        // Use the category already read, without attaching a detached graph or querying again.
        return product.ToResponse(category.Name);
    }

    public async Task<ProductResponseDto> UpdateProductAsync(int id, UpdateProductDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(updateValidator, dto, cancellationToken);
        var product = await RequireProductForUpdateAsync(id, cancellationToken);
        RequireCurrentVersion(product, dto.RowVersion);
        var previousImageUrls = product.Images.Select(image => image.ImageUrl).ToArray();
        var sku = dto.SKU.Trim();
        if (await products.ExistsBySkuAsync(sku, id, cancellationToken))
            throw new ConflictException($"SKU '{sku}' already exists.");
        // An existing product must remain manageable after its category is deactivated.
        // Customer visibility still requires an active category in the repository queries.
        var category = await RequireCategoryAsync(dto.CategoryId, cancellationToken,
            allowInactive: product.CategoryId == dto.CategoryId);
        RequestValidation.ApplyDomainRules(() =>
        {
            product.UpdateDetails(dto.Name, dto.Description, sku, dto.Brand,
                dto.Price, dto.DiscountPrice, dto.Stock, dto.CategoryId, dto.IsActive);
            return product;
        });
        if (dto.ImagesSpecified) ReplaceImages(product, dto.Images);
        await using var imageLease = await imageStore.AcquireAsync(previousImageUrls,
            product.Images.Select(image => image.ImageUrl), cancellationToken);
        products.Update(product, dto.RowVersion);
        await products.SaveChangesAsync(cancellationToken);
        await imageLease.CommitAsync(CancellationToken.None);
        return product.ToResponse(category.Name);
    }

    public async Task<ProductResponseDto> GetProductByIdAsync(int id, bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var product = await products.GetByIdAsync(id, includeInactive, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), id);
        return product.ToResponse();
    }

    public async Task<PagedResult<ProductListDto>> GetProductsAsync(ProductQueryDto query, bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(queryValidator, query, cancellationToken);
        var page = await products.GetAllAsync(query, includeInactive, cancellationToken);
        return new PagedResult<ProductListDto>(page.Items.Select(product => product.ToListItem()).ToArray(),
            page.TotalCount, page.PageNumber, page.PageSize);
    }

    public async Task DeleteProductAsync(int id, byte[] rowVersion, CancellationToken cancellationToken = default)
    {
        if (rowVersion is not { Length: 8 })
            throw new ValidationException(nameof(rowVersion), "An eight-byte rowversion is required.");
        var product = await RequireProductForUpdateAsync(id, cancellationToken);
        RequireCurrentVersion(product, rowVersion);
        product.SoftDelete();
        products.SoftDelete(product, rowVersion);
        await products.SaveChangesAsync(cancellationToken);
    }

    public Task<ProductSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        products.GetSummaryAsync(cancellationToken);

    public async Task<ProductResponseDto> UpdateStockAsync(int id, UpdateStockDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(stockValidator, dto, cancellationToken);
        var product = await RequireProductForUpdateAsync(id, cancellationToken);
        RequireCurrentVersion(product, dto.RowVersion);
        RequestValidation.ApplyDomainRules(() => { product.AdjustStock(dto.QuantityChange); return product; });
        products.Update(product, dto.RowVersion);
        await products.SaveChangesAsync(cancellationToken);
        return product.ToResponse();
    }

    private async Task<Category> RequireCategoryAsync(int id, CancellationToken cancellationToken, bool allowInactive = false)
    {
        var category = await categories.GetByIdAsync(id, includeInactive: true, cancellationToken: cancellationToken)
            ?? throw new NotFoundException(nameof(Category), id);
        if (!category.IsActive && !allowInactive)
            throw new ValidationException(nameof(CreateProductDto.CategoryId), "An active category is required.");
        return category;
    }

    private async Task<Product> RequireProductForUpdateAsync(int id, CancellationToken cancellationToken) =>
        await products.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(Product), id);

    private static void RequireCurrentVersion(Product product, byte[] expectedVersion)
    {
        if (!product.RowVersion.AsSpan().SequenceEqual(expectedVersion))
            throw new ConflictException("The product changed. Reload it before making another change.");
    }

    private static void ReplaceImages(Product product, IEnumerable<ProductImageRequestDto> images)
    {
        product.Images.Clear();
        foreach (var image in images)
            product.Images.Add(new ProductImage { ImageUrl = image.ImageUrl.Trim(), IsPrimary = image.IsPrimary, SortOrder = image.SortOrder });
        if (product.Images.Count > 0 && !product.Images.Any(image => image.IsPrimary))
            product.Images.OrderBy(image => image.SortOrder).First().IsPrimary = true;
    }
}
