using System.ComponentModel.DataAnnotations;
using TechStore.Application.Common;

namespace TechStore.Application.Validators.Products;

public sealed class ProductImageUrlAttribute : ValidationAttribute
{
    public ProductImageUrlAttribute() : base("Use an HTTP(S) image URL or an uploaded product-image URL.") { }
    public override bool IsValid(object? value) => value is null || value is string url && ProductImagePolicy.IsAllowedUrl(url);
}
