using TechStore.Application.Common;

namespace TechStore.Application.DTOs.Analytics;

public sealed class AnalyticsResponseDto
{
    public int PeriodDays { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public DateTime CartTrackingStartedAtUtc { get; init; }
    public bool CartCoverageComplete { get; init; }
    public AnalyticsOverviewDto Overview { get; init; } = new(0, 0, 0, 0, 0);
    public IReadOnlyList<ProductPerformanceDto> MostAdded { get; init; } = [];
    public IReadOnlyList<ProductPerformanceDto> MostOrdered { get; init; } = [];
    public PagedResult<ProductPerformanceDto> Performance { get; init; } = new([], 0, 1, 20);
}

public sealed record AnalyticsOverviewDto(long TotalOrders, long ConfirmedOrders, decimal SalesAmount,
    long UnitsSold, decimal AverageOrderValue);

public sealed class ProductPerformanceDto
{
    public int ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public bool IsActive { get; init; }
    public bool IsDeleted { get; init; }
    public long CartAdditions { get; init; }
    public long QuantityAdded { get; init; }
    public long UnitsSold { get; init; }
    public long OrderCount { get; init; }
}

// Repository output contains persisted facts; the application service supplies
// the reporting window, coverage policy and average order value.
public sealed record AnalyticsReadResult(DateTime CartTrackingStartedAtUtc, AnalyticsOverviewDto Overview,
    IReadOnlyList<ProductPerformanceDto> MostAdded, IReadOnlyList<ProductPerformanceDto> MostOrdered,
    PagedResult<ProductPerformanceDto> Performance);
