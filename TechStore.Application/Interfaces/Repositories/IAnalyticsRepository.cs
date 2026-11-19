using TechStore.Application.DTOs.Analytics;

namespace TechStore.Application.Interfaces.Repositories;

public interface IAnalyticsRepository
{
    Task<AnalyticsReadResult> GetAsync(DateTime? fromUtc, DateTime toUtc, int pageNumber, int pageSize,
        CancellationToken cancellationToken = default);
}
