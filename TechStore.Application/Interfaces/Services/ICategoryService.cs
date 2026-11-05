using TechStore.Application.DTOs.Categories;

namespace TechStore.Application.Interfaces.Services;

public interface ICategoryService
{
    Task<CategoryResponseDto> CreateCategoryAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default);
    Task<CategoryResponseDto> UpdateCategoryAsync(int id, UpdateCategoryDto dto, CancellationToken cancellationToken = default);
    Task<CategoryResponseDto> GetCategoryByIdAsync(int id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CategoryResponseDto>> GetCategoriesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
    Task DeactivateCategoryAsync(int id, CancellationToken cancellationToken = default);
}
