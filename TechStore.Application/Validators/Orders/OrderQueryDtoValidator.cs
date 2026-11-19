using FluentValidation;
using TechStore.Application.DTOs.Orders;

namespace TechStore.Application.Validators.Orders;

public sealed class OrderQueryDtoValidator : AbstractValidator<OrderQueryDto>
{
    public OrderQueryDtoValidator()
    {
        RuleFor(dto => dto.PageNumber).GreaterThan(0);
        RuleFor(dto => dto.PageSize).InclusiveBetween(1, 100);
        RuleFor(dto => dto).Must(dto => ((long)dto.PageNumber - 1) * dto.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
        RuleFor(dto => dto.Search).MaximumLength(100);
        RuleFor(dto => dto.Status).IsInEnum().When(dto => dto.Status.HasValue);
        RuleFor(dto => dto.FromDateUtc).Must(date => date!.Value.Kind == DateTimeKind.Utc)
            .When(dto => dto.FromDateUtc.HasValue).WithMessage("The start date must use UTC.");
        RuleFor(dto => dto.ToDateUtc).Must(date => date!.Value.Kind == DateTimeKind.Utc)
            .When(dto => dto.ToDateUtc.HasValue).WithMessage("The end date must use UTC.");
        RuleFor(dto => dto.ToDateUtc).GreaterThanOrEqualTo(dto => dto.FromDateUtc)
            .When(dto => dto.FromDateUtc.HasValue && dto.ToDateUtc.HasValue);
    }
}
