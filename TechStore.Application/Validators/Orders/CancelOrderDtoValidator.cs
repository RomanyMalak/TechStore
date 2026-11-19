using FluentValidation;
using TechStore.Application.DTOs.Orders;

namespace TechStore.Application.Validators.Orders;

public sealed class CancelOrderDtoValidator : AbstractValidator<CancelOrderDto>
{
    public CancelOrderDtoValidator()
    {
        RuleFor(dto => dto.ExpectedStatus).NotNull().IsInEnum();
        RuleFor(dto => dto.Note).MaximumLength(1000);
    }
}
