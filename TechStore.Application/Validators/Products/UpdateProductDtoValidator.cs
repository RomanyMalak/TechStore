using FluentValidation;
using TechStore.Application.DTOs.Products;

namespace TechStore.Application.Validators.Products;

public sealed class UpdateProductDtoValidator : ProductRules<UpdateProductDto>
{
    public UpdateProductDtoValidator()
    {
        RuleFor(dto => dto.RowVersion).Must(version => version is { Length: 8 })
            .WithMessage("An eight-byte rowversion is required.");
    }
}
