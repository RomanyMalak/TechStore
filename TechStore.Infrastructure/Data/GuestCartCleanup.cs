using Microsoft.EntityFrameworkCore;
using TechStore.Infrastructure.Persistence;

namespace TechStore.Infrastructure.Data;

public sealed class GuestCartCleanup(ApplicationDbContext context) : IGuestCartCleanup
{
    public Task<int> DeleteExpiredBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        if (cutoffUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The retention cutoff must use UTC.", nameof(cutoffUtc));
        if (batchSize is < 1 or > GuestCartCleanupOptions.MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        // The predicate and bounded deletion execute as one SQL statement. An ID
        // selection followed by a separate delete could remove a recently updated cart.
        // Last activity means a cart mutation; browsing/GET does not extend retention.
        // CartItems are removed by the existing database cascade, never by loading them.
        return context.Carts
            .Where(cart => cart.UserId == null && cart.GuestKey != null
                && (cart.UpdatedAt ?? cart.CreatedAt) < cutoffUtc)
            .OrderBy(cart => cart.Id)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
