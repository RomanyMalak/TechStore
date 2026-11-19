using FluentValidation;
using TechStore.Application.DTOs.Analytics;

namespace TechStore.Application.Validators.Analytics;

public sealed class AnalyticsQueryDtoValidator : AbstractValidator<AnalyticsQueryDto>
{
    public AnalyticsQueryDtoValidator()
    {
        RuleFor(query => query.PeriodDays).Must(days => days is 0 or 7 or 30 or 90)
            .WithMessage("The period must be 0, 7, 30 or 90 days.");
        RuleFor(query => query.PageNumber).GreaterThan(0);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query).Must(query => ((long)query.PageNumber - 1) * query.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
    }
}
