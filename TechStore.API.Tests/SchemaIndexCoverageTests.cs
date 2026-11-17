using Microsoft.EntityFrameworkCore;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests;

public class SchemaIndexCoverageTests
{
    [Theory]
    [InlineData(typeof(Address), nameof(Address.UserId))]
    [InlineData(typeof(ProductImage), nameof(ProductImage.ProductId))]
    public void ForeignKeyLookups_AreIndexedForAllRows(Type entityType, string foreignKey)
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var indexes = context.Model.FindEntityType(entityType)!.GetIndexes().ToArray();
        Assert.Contains(indexes, index => index.Properties[0].Name == foreignKey && index.GetFilter() is null);
        Assert.Contains(indexes, index => index.IsUnique && index.GetFilter() is not null);
    }
}
