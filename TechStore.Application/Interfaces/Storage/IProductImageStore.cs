using TechStore.Application.DTOs.Products;

namespace TechStore.Application.Interfaces.Storage;

public interface IProductImageStore
{
    Task<ProductImageUploadDto> UploadAsync(Stream content, string fileName, string contentType, string ownerId,
        CancellationToken cancellationToken = default);
    Task<ProductImageRead?> OpenReadAsync(string fileName, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid id, string ownerId, CancellationToken cancellationToken = default);
    Task<IProductImageLease> AcquireAsync(IEnumerable<string> previousUrls, IEnumerable<string> nextUrls,
        CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredDraftsAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);
}

public interface IProductImageLease : IAsyncDisposable
{
    // Called only after the product's SQL transaction has committed. Failed
    // persistence leaves old files intact; disposal only releases the locks.
    Task CommitAsync(CancellationToken cancellationToken = default);
}
