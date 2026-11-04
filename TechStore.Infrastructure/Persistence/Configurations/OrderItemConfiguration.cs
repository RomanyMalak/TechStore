using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems", table =>
        {
            table.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_OrderItems_UnitPrice", "[UnitPrice] > 0");
            table.HasCheckConstraint("CK_OrderItems_LineTotal", "[LineTotal] = [UnitPrice] * [Quantity]");
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(item => item.SKU).HasMaxLength(64).IsRequired();
        builder.Property(item => item.UnitPrice).HasPrecision(18, 2);
        builder.Property(item => item.LineTotal).HasPrecision(18, 2);

        builder.HasOne(item => item.Order)
            .WithMany(order => order.Items)
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Keep the reference and purchase snapshots; use Product.IsDeleted for retirement.
        builder.HasOne(item => item.Product)
            .WithMany()
            .HasForeignKey(item => item.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
