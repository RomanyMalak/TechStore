using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Categories;

public class CreateCategoryDto
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Range(1, int.MaxValue)]
    public int? ParentCategoryId { get; set; }

    public bool IsActive { get; set; } = true;
}
