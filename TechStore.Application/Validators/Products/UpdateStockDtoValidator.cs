using FluentValidation;
using TechStore.Application.DTOs.Products;

namespace TechStore.Application.Validators.Products;

public sealed class UpdateStockDtoValidator : AbstractValidator<UpdateStockDto>
{
    public UpdateStockDtoValidator()
    {
        RuleFor(dto => dto.QuantityChange).NotEqual(0);
        RuleFor(dto => dto.RowVersion).Must(version => version is { Length: 8 })
            .WithMessage("An eight-byte rowversion is required.");
    }
}
