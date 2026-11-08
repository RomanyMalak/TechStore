using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Mapping;
using TechStore.Domain.Entities;
using ValidationException = TechStore.Application.Exceptions.ValidationException;

namespace TechStore.Application.Services;

public sealed class CategoryService(
    ICategoryRepository categories,
    IValidator<CreateCategoryDto> createValidator,
    IValidator<UpdateCategoryDto> updateValidator) : ICategoryService
{
    public async Task<CategoryResponseDto> CreateCategoryAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(createValidator, dto, cancellationToken);
        return await categories.ExecuteInTransactionAsync(async token =>
        {
            await RequireUniqueNameAsync(dto.Name.Trim(), null, token);
            var all = await categories.GetAllAsync(includeInactive: true, cancellationToken: token);
            var parent = ValidateParent(all, null, dto.ParentCategoryId, dto.IsActive);
            var category = RequestValidation.ApplyDomainRules(() =>
            {
                var entity = new Category();
                entity.UpdateDetails(dto.Name, dto.Description, dto.ParentCategoryId, dto.IsActive);
                return entity;
            });
            await categories.AddAsync(category, token);
            await categories.SaveChangesAsync(token);
            return category.ToResponse(parent?.Name);
        }, cancellationToken);
    }

    public async Task<CategoryResponseDto> UpdateCategoryAsync(int id, UpdateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(updateValidator, dto, cancellationToken);
        return await categories.ExecuteInTransactionAsync(async token =>
        {
            var category = await categories.GetForUpdateAsync(id, token) ?? throw new NotFoundException(nameof(Category), id);
            await RequireUniqueNameAsync(dto.Name.Trim(), id, token);
            var all = await categories.GetAllAsync(includeInactive: true, cancellationToken: token);
            var parent = ValidateParent(all, id, dto.ParentCategoryId, dto.IsActive);
            if (!dto.IsActive) RequireNoActiveChildren(all, id);
            RequestValidation.ApplyDomainRules(() =>
            {
                category.UpdateDetails(dto.Name, dto.Description, dto.ParentCategoryId, dto.IsActive);
                return category;
            });
            categories.Update(category);
            await categories.SaveChangesAsync(token);
            return category.ToResponse(parent?.Name);
        }, cancellationToken);
    }

    public async Task<CategoryResponseDto> GetCategoryByIdAsync(int id, bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var category = await categories.GetByIdAsync(id, includeInactive, cancellationToken)
            ?? throw new NotFoundException(nameof(Category), id);
        return category.ToResponse();
    }

    public async Task<IReadOnlyList<CategoryResponseDto>> GetCategoriesAsync(bool includeInactive = false,
        CancellationToken cancellationToken = default) =>
        (await categories.GetAllAsync(includeInactive, cancellationToken)).Select(category => category.ToResponse()).ToArray();

    public async Task DeactivateCategoryAsync(int id, CancellationToken cancellationToken = default)
    {
        await categories.ExecuteInTransactionAsync(async token =>
        {
            var category = await categories.GetForUpdateAsync(id, token) ?? throw new NotFoundException(nameof(Category), id);
            var all = await categories.GetAllAsync(includeInactive: true, cancellationToken: token);
            RequireNoActiveChildren(all, id);
            category.Deactivate();
            categories.Deactivate(category);
            await categories.SaveChangesAsync(token);
            return true;
        }, cancellationToken);
    }

    private async Task RequireUniqueNameAsync(string name, int? excludingId, CancellationToken cancellationToken)
    {
        if (await categories.ExistsByNameAsync(name, excludingId, cancellationToken))
            throw new ConflictException($"Category name '{name}' already exists.");
    }

    private static Category? ValidateParent(IReadOnlyList<Category> all, int? categoryId, int? parentId, bool isActive)
    {
        if (!parentId.HasValue) return null;
        var byId = all.ToDictionary(category => category.Id);
        if (!byId.TryGetValue(parentId.Value, out var parent))
            throw new NotFoundException(nameof(Category), parentId.Value);

        var visited = new HashSet<int>();
        int? currentId = parentId;
        while (currentId.HasValue)
        {
            if (currentId == categoryId || !visited.Add(currentId.Value))
                throw new ValidationException(nameof(CreateCategoryDto.ParentCategoryId), "A category hierarchy cannot contain cycles or use itself as a parent.");
            if (!byId.TryGetValue(currentId.Value, out var ancestor))
                throw new ValidationException(nameof(CreateCategoryDto.ParentCategoryId), "The parent hierarchy contains a missing category.");
            if (isActive && !ancestor.IsActive)
                throw new ValidationException(nameof(CreateCategoryDto.ParentCategoryId), "An active category cannot have an inactive ancestor.");
            currentId = ancestor.ParentCategoryId;
        }
        return parent;
    }

    private static void RequireNoActiveChildren(IReadOnlyList<Category> all, int id)
    {
        if (all.Any(category => category.ParentCategoryId == id && category.IsActive))
            throw new ConflictException("Deactivate active subcategories before deactivating their parent.");
    }
}
