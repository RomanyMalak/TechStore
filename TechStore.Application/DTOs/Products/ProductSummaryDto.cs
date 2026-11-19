namespace TechStore.Application.DTOs.Products;

public sealed class ProductSummaryDto
{
    public int TotalProducts { get; set; }
    public int ActiveProducts { get; set; }
    public int InStockProducts { get; set; }
    public int OutOfStockProducts { get; set; }
}
