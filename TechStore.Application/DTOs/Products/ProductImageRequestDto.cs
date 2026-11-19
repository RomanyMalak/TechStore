using System.ComponentModel.DataAnnotations;
using TechStore.Application.Validators.Products;

namespace TechStore.Application.DTOs.Products;

public class ProductImageRequestDto
{
    [Required, ProductImageUrl, StringLength(2048)]
    public string ImageUrl { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    [Range(0, int.MaxValue)]
    public int SortOrder { get; set; }
}
