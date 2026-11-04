using TechStore.Domain.Exceptions;

namespace TechStore.Domain.Entities;

public class Product
{
    public const int SkuMaxLength = 50;
    public const decimal MaximumPrice = 9_999_999_999_999_999.99m;

    private Product() { }

    public int Id { get; set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string SKU { get; private set; } = string.Empty;
    public string Brand { get; private set; } = string.Empty;
    public decimal Price { get; private set; }
    public decimal? DiscountPrice { get; private set; }
    public int Stock { get; private set; }
    public int CategoryId { get; private set; }
    public Category Category { get; set; } = null!;
    public ICollection<ProductImage> Images { get; set; } = new List<ProductImage>();
    public bool IsActive { get; private set; } = true;
    public bool IsDeleted { get; private set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; private set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public static Product Create(string name, string description, string sku, string brand,
        decimal price, decimal? discountPrice, int stock, int categoryId, bool isActive = true)
    {
        var product = new Product();
        product.UpdateDetails(name, description, sku, brand, price, discountPrice, stock, categoryId, isActive);
        product.UpdatedAt = null;
        return product;
    }

    public void UpdateDetails(string name, string description, string sku, string brand,
        decimal price, decimal? discountPrice, int stock, int categoryId, bool isActive)
    {
        var validName = RequiredText(name, 200, nameof(Name));
        var validDescription = RequiredText(description, 4000, nameof(Description));
        var validSku = RequiredText(sku, SkuMaxLength, nameof(SKU));
        var validBrand = RequiredText(brand, 100, nameof(Brand));
        ValidatePrice(price, nameof(Price));
        if (discountPrice.HasValue)
        {
            ValidatePrice(discountPrice.Value, nameof(DiscountPrice));
            if (discountPrice >= price)
                throw new DomainRuleException(nameof(DiscountPrice), "Discount price must be less than price.");
        }
        if (stock < 0) throw new DomainRuleException(nameof(Stock), "Stock cannot be negative.");
        if (categoryId <= 0) throw new DomainRuleException(nameof(CategoryId), "Category is required.");

        Name = validName;
        Description = validDescription;
        SKU = validSku;
        Brand = validBrand;
        Price = price;
        DiscountPrice = discountPrice;
        Stock = stock;
        CategoryId = categoryId;
        IsActive = isActive;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AdjustStock(int quantityChange)
    {
        var newStock = (long)Stock + quantityChange;
        if (quantityChange == 0 || newStock < 0 || newStock > int.MaxValue)
            throw new DomainRuleException(nameof(Stock), "Stock change must be non-zero and leave stock between zero and Int32.MaxValue.");
        Stock = (int)newStock;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    private static string RequiredText(string value, int maximumLength, string property)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength)
            throw new DomainRuleException(property, $"{property} is required and cannot exceed {maximumLength} characters.");
        return value.Trim();
    }

    private static void ValidatePrice(decimal value, string property)
    {
        if (value <= 0 || value > MaximumPrice || decimal.Round(value, 2) != value)
            throw new DomainRuleException(property, "Price must be positive and fit decimal(18,2).");
    }
}
