using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Persistence;

namespace TechStore.Infrastructure.Data;

public sealed class TechStoreDataSeeder(ApplicationDbContext context, ILogger<TechStoreDataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Keep both saves atomic while letting SQL Server generate the category IDs first.
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var categories = new Dictionary<string, Category>();
        var addedCategories = 0;
        foreach (var seed in CategorySeeds)
        {
            // Equality uses the database's collation, just like the unique name index.
            var category = await context.Categories.AsNoTracking()
                .SingleOrDefaultAsync(category => category.Name == seed.Name, cancellationToken);
            if (category is null)
            {
                category = new Category();
                category.UpdateDetails(seed.Name, seed.Description, parentCategoryId: null, isActive: true);
                context.Categories.Add(category);
                addedCategories++;
            }
            categories.Add(seed.Name, category);
        }

        if (addedCategories > 0) await context.SaveChangesAsync(cancellationToken);

        var addedProducts = 0;
        foreach (var seed in ProductSeeds)
        {
            // Include inactive and soft-deleted products: their SKUs still belong to the user.
            if (await context.Products.AsNoTracking().AnyAsync(product => product.SKU == seed.Sku, cancellationToken))
                continue;

            var presentation = Presentations[seed.Sku];
            var product = Product.Create(seed.Name, presentation.Description, seed.Sku, seed.Brand,
                seed.Price, seed.DiscountPrice, seed.Stock, categories[seed.CategoryName].Id, isActive: true);
            product.Images.Add(new ProductImage { ImageUrl = presentation.ImageUrl, IsPrimary = true, SortOrder = 0 });
            context.Products.Add(product);
            addedProducts++;
        }

        if (addedProducts > 0) await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Development catalog seeding completed: added {CategoryCount} categories and {ProductCount} products.",
            addedCategories, addedProducts);
    }

    private static readonly CategorySeed[] CategorySeeds =
    [
        new("Electronics", "Smartphones and everyday consumer electronics."),
        new("Accessories", "Computer peripherals, audio and gaming accessories."),
        new("Laptops", "Laptops for work, study and everyday productivity.")
    ];

    private static readonly ProductSeed[] ProductSeeds =
    [
        new("iPhone 15", "IPH-15-001", "Apple", 40000m, 37000m, 15, "Electronics"),
        new("Samsung Galaxy S24", "SAM-S24-001", "Samsung", 35000m, null, 8, "Electronics"),
        new("Logitech Mouse", "LOG-MOU-001", "Logitech", 1200m, 950m, 50, "Accessories"),
        new("HyperX Headset", "HYP-HEAD-001", "HyperX", 3500m, null, 0, "Accessories"),
        new("Lenovo ThinkPad", "LEN-THK-001", "Lenovo", 45000m, 42000m, 5, "Laptops")
    ];

    private sealed record CategorySeed(string Name, string Description);
    private sealed record ProductSeed(string Name, string Sku, string Brand,
        decimal Price, decimal? DiscountPrice, int Stock, string CategoryName);
    private sealed record Presentation(string Sku, string Description, string ImageUrl);
    private static readonly IReadOnlyDictionary<string, Presentation> Presentations = ReadPresentations();
    private static IReadOnlyDictionary<string, Presentation> ReadPresentations()
    {
        using var resource = typeof(TechStoreDataSeeder).Assembly.GetManifestResourceStream("TechStore.Infrastructure.Data.CatalogPresentation.json")
            ?? throw new InvalidOperationException("Development catalog presentation resource is missing.");
        return (JsonSerializer.Deserialize<Presentation[]>(resource, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Development catalog presentation is invalid.")).ToDictionary(item => item.Sku);
    }
}
