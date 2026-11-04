namespace TechStore.Application.DTOs.Categories;

public class CategoryResponseDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? ParentCategoryId { get; set; }
    public string? ParentCategoryName { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
