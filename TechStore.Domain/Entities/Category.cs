using TechStore.Domain.Exceptions;

namespace TechStore.Domain.Entities;

public class Category
{
    public const int DescriptionMaxLength = 500;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }
    public ICollection<Category> SubCategories { get; set; } = new List<Category>();
    public ICollection<Product> Products { get; set; } = new List<Product>();
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public void UpdateDetails(string name, string? description, int? parentCategoryId, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new DomainRuleException(nameof(Name), "Category name is required and cannot exceed 100 characters.");
        if (description?.Length > DescriptionMaxLength)
            throw new DomainRuleException(nameof(Description), "Category description cannot exceed 500 characters.");
        if (parentCategoryId <= 0 || (Id != 0 && parentCategoryId == Id))
            throw new DomainRuleException(nameof(ParentCategoryId), "A category must have a valid parent other than itself.");

        Name = name.Trim();
        Description = description?.Trim();
        ParentCategoryId = parentCategoryId;
        IsActive = isActive;
    }

    public void Deactivate() => IsActive = false;
}
