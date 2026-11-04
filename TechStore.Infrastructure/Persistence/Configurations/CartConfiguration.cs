using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Identity;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        builder.ToTable("Carts");
        builder.HasKey(cart => cart.Id);
        builder.Property(cart => cart.UserId).HasMaxLength(450).IsRequired();
        builder.Property(cart => cart.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(cart => cart.UserId).IsUnique();

        builder.HasOne<ApplicationUser>()
            .WithOne(user => user.Cart)
            .HasForeignKey<Cart>(cart => cart.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
