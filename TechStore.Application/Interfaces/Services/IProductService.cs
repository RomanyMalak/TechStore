using TechStore.Application.Common;
using TechStore.Application.DTOs.Products;

namespace TechStore.Application.Interfaces.Services;

public interface IProductService
{
    Task<ProductResponseDto> CreateProductAsync(CreateProductDto dto, CancellationToken cancellationToken = default);
    Task<ProductResponseDto> UpdateProductAsync(int id, UpdateProductDto dto, CancellationToken cancellationToken = default);
    Task<ProductResponseDto> GetProductByIdAsync(int id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<PagedResult<ProductListDto>> GetProductsAsync(ProductQueryDto query, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task DeleteProductAsync(int id, byte[] rowVersion, CancellationToken cancellationToken = default);
    Task<ProductResponseDto> UpdateStockAsync(int id, UpdateStockDto dto, CancellationToken cancellationToken = default);
}
