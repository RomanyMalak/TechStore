namespace TechStore.Infrastructure.Data;

// Infrastructure maintenance, rather than a customer-facing business use case.
public interface IGuestCartCleanup
{
    Task<int> DeleteExpiredBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);
}
