using TechStore.Application.Common;
using TechStore.Application.DTOs.Products;
using TechStore.Domain.Entities;

namespace TechStore.Application.Interfaces.Repositories;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<Product?> GetForUpdateAsync(int id, CancellationToken cancellationToken = default);
    Task<PagedResult<Product>> GetAllAsync(ProductQueryDto query, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<ProductSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<bool> IsImageReferencedAsync(string imageUrl, CancellationToken cancellationToken = default);
    Task AddAsync(Product product, CancellationToken cancellationToken = default);
    void Update(Product product, byte[] expectedRowVersion);
    void SoftDelete(Product product, byte[] expectedRowVersion);
    Task<bool> ExistsBySkuAsync(string sku, int? excludingId = null, CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
