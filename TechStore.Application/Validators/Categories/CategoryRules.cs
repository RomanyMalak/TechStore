using FluentValidation;
using TechStore.Application.DTOs.Categories;
using TechStore.Domain.Entities;

namespace TechStore.Application.Validators.Categories;

public abstract class CategoryRules<T> : AbstractValidator<T> where T : CreateCategoryDto
{
    protected CategoryRules()
    {
        RuleFor(dto => dto.Name).NotEmpty().MaximumLength(100);
        RuleFor(dto => dto.Description).MaximumLength(Category.DescriptionMaxLength);
        RuleFor(dto => dto.ParentCategoryId).GreaterThan(0).When(dto => dto.ParentCategoryId.HasValue);
    }
}
