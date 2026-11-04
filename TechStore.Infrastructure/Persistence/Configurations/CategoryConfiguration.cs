using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories", table =>
            table.HasCheckConstraint("CK_Categories_NotOwnParent", "[ParentCategoryId] IS NULL OR [ParentCategoryId] <> [Id]"));

        builder.HasKey(category => category.Id);
        builder.Property(category => category.Name).HasMaxLength(100).IsRequired();
        builder.Property(category => category.Description).HasMaxLength(Category.DescriptionMaxLength);
        builder.HasIndex(category => category.Name).IsUnique();
        builder.Property(category => category.IsActive).HasDefaultValue(true);
        builder.Property(category => category.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");

        builder.HasOne(category => category.ParentCategory)
            .WithMany(category => category.SubCategories)
            .HasForeignKey(category => category.ParentCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
