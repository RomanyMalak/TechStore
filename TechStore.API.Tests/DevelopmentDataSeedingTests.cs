using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Data;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests;

public class DevelopmentDataSeedingTests
{
    [Fact]
    public async Task EmptyCatalog_SeedsRequestedProductsWithGeneratedCategoryIdsAndPrimaryImages()
    {
        await using var context = CreateContext();
        // Generated category IDs must work even when 1, 2 and 3 are not available.
        var unrelated = new Category { Id = 100 };
        unrelated.UpdateDetails("Existing category", "User data", null, true);
        context.Categories.Add(unrelated);
        await context.SaveChangesAsync();

        await CreateSeeder(context).SeedAsync();
        context.ChangeTracker.Clear();
        var categories = await context.Categories.Where(category => category.Id != 100).ToDictionaryAsync(category => category.Name);
        Assert.Equal(new[] { "Accessories", "Electronics", "Laptops" }, categories.Keys.Order().ToArray());
        Assert.All(categories.Values, category => { Assert.True(category.IsActive); Assert.True(category.Id > 100); });
        var products = await context.Products.Include(product => product.Images).ToDictionaryAsync(product => product.SKU);
        Assert.Equal(5, products.Count);
        AssertProduct(products["IPH-15-001"], "iPhone 15", "Apple", 40000m, 37000m, 15, categories["Electronics"].Id);
        AssertProduct(products["SAM-S24-001"], "Samsung Galaxy S24", "Samsung", 35000m, null, 8, categories["Electronics"].Id);
        AssertProduct(products["LOG-MOU-001"], "Logitech Mouse", "Logitech", 1200m, 950m, 50, categories["Accessories"].Id);
        AssertProduct(products["HYP-HEAD-001"], "HyperX Headset", "HyperX", 3500m, null, 0, categories["Accessories"].Id);
        AssertProduct(products["LEN-THK-001"], "Lenovo ThinkPad", "Lenovo", 45000m, 42000m, 5, categories["Laptops"].Id);
    }

    [Fact]
    public async Task RepeatedSeeding_PreservesIdsTimestampsAndImageCounts()
    {
        await using var context = CreateContext();
        await CreateSeeder(context).SeedAsync();
        context.ChangeTracker.Clear();
        var before = await context.Products.AsNoTracking().OrderBy(product => product.Id)
            .Select(product => new { product.Id, product.SKU, product.CreatedAt, product.UpdatedAt }).ToArrayAsync();
        var imageIds = await context.ProductImages.OrderBy(image => image.Id).Select(image => image.Id).ToArrayAsync();
        var categoryIds = await context.Categories.OrderBy(category => category.Id).Select(category => category.Id).ToArrayAsync();

        await CreateSeeder(context).SeedAsync();
        await CreateSeeder(context).SeedAsync();

        Assert.Equal(before, await context.Products.AsNoTracking().OrderBy(product => product.Id)
            .Select(product => new { product.Id, product.SKU, product.CreatedAt, product.UpdatedAt }).ToArrayAsync());
        Assert.Equal(imageIds, await context.ProductImages.OrderBy(image => image.Id).Select(image => image.Id).ToArrayAsync());
        Assert.Equal(categoryIds, await context.Categories.OrderBy(category => category.Id).Select(category => category.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExistingSku_PreservesUserDetailsImagesAndInactiveOrDeletedState(bool inactive, bool deleted)
    {
        await using var context = CreateContext();
        var category = new Category();
        category.UpdateDetails("Electronics", "User's category description", null, false);
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        var product = Product.Create("User's phone", "User's product description", "IPH-15-001", "User brand",
            99m, null, 2, category.Id, !inactive);
        product.Images.Add(new ProductImage { ImageUrl = "https://example.com/user-phone.png", IsPrimary = true, SortOrder = 3 });
        if (deleted) product.SoftDelete();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        var existingId = product.Id;
        var imageId = product.Images.Single().Id;
        var updatedAt = product.UpdatedAt;
        context.ChangeTracker.Clear();

        await CreateSeeder(context).SeedAsync();
        await CreateSeeder(context).SeedAsync();
        context.ChangeTracker.Clear();

        var preservedCategory = await context.Categories.SingleAsync(entity => entity.Name == "Electronics");
        Assert.Equal(category.Id, preservedCategory.Id);
        Assert.False(preservedCategory.IsActive);
        Assert.Equal("User's category description", preservedCategory.Description);
        var preserved = await context.Products.Include(entity => entity.Images).SingleAsync(entity => entity.SKU == "IPH-15-001");
        Assert.Equal(existingId, preserved.Id);
        Assert.Equal("User's phone", preserved.Name);
        Assert.Equal("User's product description", preserved.Description);
        Assert.Equal("User brand", preserved.Brand);
        Assert.Equal(99m, preserved.Price);
        Assert.Null(preserved.DiscountPrice);
        Assert.Equal(2, preserved.Stock);
        Assert.Equal(!inactive && !deleted, preserved.IsActive);
        Assert.Equal(deleted, preserved.IsDeleted);
        Assert.Equal(updatedAt, preserved.UpdatedAt);
        var image = Assert.Single(preserved.Images);
        Assert.Equal(imageId, image.Id);
        Assert.Equal("https://example.com/user-phone.png", image.ImageUrl);
        Assert.Equal(3, image.SortOrder);
        Assert.Equal(3, await context.Categories.CountAsync());
        Assert.Equal(5, await context.Products.CountAsync());
        Assert.Equal(5, await context.ProductImages.CountAsync());
    }

    [Fact]
    public async Task ExistingProductWithoutImages_IsLeftUntouched()
    {
        await using var context = CreateContext();
        var category = new Category();
        category.UpdateDetails("Electronics", null, null, true);
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        var product = Product.Create("Existing phone", "User-managed product", "IPH-15-001", "Apple", 10m, null, 1, category.Id);
        context.Products.Add(product);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await CreateSeeder(context).SeedAsync();

        Assert.False(await context.ProductImages.AnyAsync(image => image.ProductId == product.Id));
        Assert.Equal(4, await context.ProductImages.CountAsync());
    }

    [Theory]
    [InlineData("Development", 3, 5)]
    [InlineData("Production", 0, 0)]
    [InlineData("Staging", 0, 0)]
    public async Task Startup_SeedsCatalogOnlyInDevelopment(string environment, int expectedCategories, int expectedProducts)
    {
        using var factory = new ApiFactory(environment, configureAdmin: false);
        using var client = factory.Client();
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(expectedCategories, await context.Categories.CountAsync());
        Assert.Equal(expectedProducts, await context.Products.CountAsync());
        Assert.Equal(expectedProducts, await context.ProductImages.CountAsync());
    }

    [Fact]
    public async Task CancelledSeeding_DoesNotInsertData()
    {
        await using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateSeeder(context).SeedAsync(cancellation.Token));

        Assert.False(await context.Categories.AnyAsync());
        Assert.False(await context.Products.AnyAsync());
        Assert.False(await context.ProductImages.AnyAsync());
    }

    private static void AssertProduct(Product product, string name, string brand, decimal price, decimal? discount, int stock, int categoryId)
    {
        Assert.Equal(name, product.Name);
        Assert.Equal(brand, product.Brand);
        Assert.Equal(price, product.Price);
        Assert.Equal(discount, product.DiscountPrice);
        Assert.Equal(stock, product.Stock);
        Assert.Equal(categoryId, product.CategoryId);
        Assert.True(product.IsActive);
        Assert.False(product.IsDeleted);
        Assert.False(string.IsNullOrWhiteSpace(product.Description));
        var image = Assert.Single(product.Images);
        Assert.True(image.IsPrimary);
        Assert.Equal(product.Id, image.ProductId);
        Assert.Equal(0, image.SortOrder);
        var uri = new Uri(image.ImageUrl, UriKind.Absolute);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.InRange(image.ImageUrl.Length, 1, 2048);
    }

    private static ApplicationDbContext CreateContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static TechStoreDataSeeder CreateSeeder(ApplicationDbContext context) => new(context, NullLogger<TechStoreDataSeeder>.Instance);
}
