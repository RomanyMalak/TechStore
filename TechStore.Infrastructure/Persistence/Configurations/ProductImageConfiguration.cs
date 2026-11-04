using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.ToTable("ProductImages", table =>
            table.HasCheckConstraint("CK_ProductImages_SortOrder", "[SortOrder] >= 0"));

        builder.HasKey(image => image.Id);
        builder.Property(image => image.ImageUrl).HasMaxLength(2048).IsRequired();
        builder.HasIndex(image => image.ProductId)
            .HasDatabaseName("IX_ProductImages_OnePrimaryPerProduct")
            .IsUnique()
            .HasFilter("[IsPrimary] = 1");

        builder.HasOne(image => image.Product)
            .WithMany(product => product.Images)
            .HasForeignKey(image => image.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
