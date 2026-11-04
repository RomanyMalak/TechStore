using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_Price", "[Price] > 0");
            table.HasCheckConstraint("CK_Products_DiscountPrice", "[DiscountPrice] IS NULL OR ([DiscountPrice] > 0 AND [DiscountPrice] < [Price])");
            table.HasCheckConstraint("CK_Products_Stock", "[Stock] >= 0");
        });

        builder.HasKey(product => product.Id);
        builder.Property(product => product.Name).HasMaxLength(200).IsRequired();
        builder.Property(product => product.Description).HasMaxLength(4000).IsRequired();
        builder.Property(product => product.SKU).HasMaxLength(Product.SkuMaxLength).IsRequired();
        builder.Property(product => product.Brand).HasMaxLength(100).IsRequired();
        builder.Property(product => product.Price).HasPrecision(18, 2);
        builder.Property(product => product.DiscountPrice).HasPrecision(18, 2);
        builder.Property(product => product.IsActive).HasDefaultValue(true);
        builder.Property(product => product.IsDeleted).HasDefaultValue(false);
        builder.Property(product => product.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(product => product.RowVersion).IsRowVersion();
        builder.HasIndex(product => product.SKU).IsUnique();

        builder.HasOne(product => product.Category)
            .WithMany(category => category.Products)
            .HasForeignKey(product => product.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // IsDeleted is explicit: future catalog queries must filter it themselves.
        // A global filter on this required navigation could hide historical order items.
    }
}
