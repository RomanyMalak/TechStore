namespace TechStore.Application.DTOs.Products;

public enum ProductSort
{
    Newest = 0,
    NameAscending = 1,
    NameDescending = 2,
    PriceAscending = 3,
    PriceDescending = 4
}

public class ProductQueryDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public int? CategoryId { get; set; }
    public string? Brand { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool InStockOnly { get; set; }
    public ProductSort SortBy { get; set; } = ProductSort.Newest;
}
