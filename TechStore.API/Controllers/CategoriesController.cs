using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController(ICategoryService categories) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponseDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await categories.GetCategoriesAsync(User.IsInRole(Roles.Admin), cancellationToken));

    [HttpGet("{id:int:min(1)}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CategoryResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CategoryResponseDto>> GetById(int id, CancellationToken cancellationToken) =>
        Ok(await categories.GetCategoryByIdAsync(id, User.IsInRole(Roles.Admin), cancellationToken));

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(CategoryResponseDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<CategoryResponseDto>> Create(CreateCategoryDto dto, CancellationToken cancellationToken)
    {
        var category = await categories.CreateCategoryAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = category.Id }, category);
    }

    [HttpPut("{id:int:min(1)}")]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(CategoryResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CategoryResponseDto>> Update(int id, UpdateCategoryDto dto, CancellationToken cancellationToken) =>
        Ok(await categories.UpdateCategoryAsync(id, dto, cancellationToken));

    [HttpDelete("{id:int:min(1)}")]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(int id, CancellationToken cancellationToken)
    {
        await categories.DeactivateCategoryAsync(id, cancellationToken);
        return NoContent();
    }
}
