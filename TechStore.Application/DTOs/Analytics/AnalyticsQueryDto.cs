namespace TechStore.Application.DTOs.Analytics;

public sealed class AnalyticsQueryDto
{
    public int PeriodDays { get; set; } = 30;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
