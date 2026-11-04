using FluentValidation;
using TechStore.Application.DTOs.Products;

namespace TechStore.Application.Validators.Products;

public sealed class ProductQueryDtoValidator : AbstractValidator<ProductQueryDto>
{
    public ProductQueryDtoValidator()
    {
        RuleFor(dto => dto.PageNumber).GreaterThan(0);
        RuleFor(dto => dto.PageSize).InclusiveBetween(1, 100);
        RuleFor(dto => dto).Must(dto => ((long)dto.PageNumber - 1) * dto.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
        RuleFor(dto => dto.Search).MaximumLength(200);
        RuleFor(dto => dto.Brand).MaximumLength(100);
        RuleFor(dto => dto.CategoryId).GreaterThan(0).When(dto => dto.CategoryId.HasValue);
        RuleFor(dto => dto.MinPrice).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true).When(dto => dto.MinPrice.HasValue);
        RuleFor(dto => dto.MaxPrice).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true).When(dto => dto.MaxPrice.HasValue);
        RuleFor(dto => dto.MaxPrice).GreaterThanOrEqualTo(dto => dto.MinPrice)
            .When(dto => dto.MinPrice.HasValue && dto.MaxPrice.HasValue);
        RuleFor(dto => dto.SortBy).IsInEnum();
    }
}
