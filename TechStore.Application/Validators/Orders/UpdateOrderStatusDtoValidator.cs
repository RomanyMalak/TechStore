using FluentValidation;
using TechStore.Application.DTOs.Orders;

namespace TechStore.Application.Validators.Orders;

public sealed class UpdateOrderStatusDtoValidator : AbstractValidator<UpdateOrderStatusDto>
{
    public UpdateOrderStatusDtoValidator()
    {
        RuleFor(dto => dto.Status).NotNull().IsInEnum();
        RuleFor(dto => dto.ExpectedStatus).NotNull().IsInEnum();
        RuleFor(dto => dto.Note).MaximumLength(1000);
    }
}
