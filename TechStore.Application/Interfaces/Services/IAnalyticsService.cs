using TechStore.Application.DTOs.Analytics;

namespace TechStore.Application.Interfaces.Services;

public interface IAnalyticsService
{
    Task<AnalyticsResponseDto> GetAsync(AnalyticsQueryDto query, CancellationToken cancellationToken = default);
}
