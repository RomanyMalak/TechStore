using FluentValidation;
using TechStore.Application.DTOs.Products;
using TechStore.Domain.Entities;
using TechStore.Application.Common;

namespace TechStore.Application.Validators.Products;

public abstract class ProductRules<T> : AbstractValidator<T> where T : CreateProductDto
{
    protected ProductRules()
    {
        RuleFor(dto => dto.Name).NotEmpty().MaximumLength(200);
        RuleFor(dto => dto.Description).NotEmpty().MaximumLength(4000);
        RuleFor(dto => dto.SKU).NotEmpty().MaximumLength(Product.SkuMaxLength);
        RuleFor(dto => dto.Brand).NotEmpty().MaximumLength(100);
        RuleFor(dto => dto.Price).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(dto => dto.DiscountPrice).GreaterThan(0).PrecisionScale(18, 2, true)
            .LessThan(dto => dto.Price).When(dto => dto.DiscountPrice.HasValue);
        RuleFor(dto => dto.Stock).GreaterThanOrEqualTo(0);
        RuleFor(dto => dto.CategoryId).GreaterThan(0);
        RuleFor(dto => dto.Images).Cascade(CascadeMode.Stop).NotNull()
            .Must(images => images.Count <= ProductImagePolicy.MaximumImages)
            .WithMessage("A product can have at most ten images.")
            .Must(images => images.Count(image => image is not null && image.IsPrimary) <= 1)
            .WithMessage("Only one image may be primary.");
        RuleForEach(dto => dto.Images).NotNull().ChildRules(image =>
        {
            image.RuleFor(dto => dto.ImageUrl).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(2048)
                .Must(ProductImagePolicy.IsAllowedUrl)
                .WithMessage("Use an HTTP(S) image URL or an uploaded product-image URL.");
            image.RuleFor(dto => dto.SortOrder).GreaterThanOrEqualTo(0);
        }).When(dto => dto.Images is not null);
    }
}
