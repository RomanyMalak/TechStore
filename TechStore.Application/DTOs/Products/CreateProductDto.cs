using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Products;

public class CreateProductDto
{
    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(4000)]
    public string Description { get; set; } = string.Empty;

    [Required, StringLength(50)]
    public string SKU { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string Brand { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal Price { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal? DiscountPrice { get; set; }

    [Range(0, int.MaxValue)]
    public int Stock { get; set; }

    [Range(1, int.MaxValue)]
    public int CategoryId { get; set; }

    public bool IsActive { get; set; } = true;

    [Required]
    public List<ProductImageRequestDto> Images { get; set; } = new();
}
