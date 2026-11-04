namespace TechStore.Application.DTOs.Products;

public class ProductImageResponseDto
{
    public int Id { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }
}
