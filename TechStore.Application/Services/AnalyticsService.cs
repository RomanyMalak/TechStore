using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Analytics;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;

namespace TechStore.Application.Services;

public sealed class AnalyticsService(IAnalyticsRepository repository, IValidator<AnalyticsQueryDto> validator,
    TimeProvider timeProvider) : IAnalyticsService
{
    public async Task<AnalyticsResponseDto> GetAsync(AnalyticsQueryDto query, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(validator, query, cancellationToken);
        var toUtc = timeProvider.GetUtcNow().UtcDateTime;
        DateTime? fromUtc = query.PeriodDays == 0 ? null : toUtc.AddDays(-query.PeriodDays);
        var data = await repository.GetAsync(fromUtc, toUtc, query.PageNumber, query.PageSize, cancellationToken);
        var trackingStartedAtUtc = DateTime.SpecifyKind(data.CartTrackingStartedAtUtc, DateTimeKind.Utc);
        return new AnalyticsResponseDto
        {
            PeriodDays = query.PeriodDays, FromUtc = fromUtc, ToUtc = toUtc,
            CartTrackingStartedAtUtc = trackingStartedAtUtc,
            CartCoverageComplete = fromUtc.HasValue && fromUtc.Value >= trackingStartedAtUtc,
            Overview = data.Overview with
            {
                AverageOrderValue = data.Overview.ConfirmedOrders == 0 ? 0m
                    : decimal.Round(data.Overview.SalesAmount / data.Overview.ConfirmedOrders, 2, MidpointRounding.AwayFromZero)
            },
            MostAdded = data.MostAdded, MostOrdered = data.MostOrdered, Performance = data.Performance
        };
    }
}
