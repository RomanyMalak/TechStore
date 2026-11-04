using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Identity;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", table =>
        {
            table.HasCheckConstraint("CK_Orders_Status", "[Status] IN (0, 1, 2, 3, 4, 5, 6)");
            table.HasCheckConstraint("CK_Orders_PaymentMethod", "[PaymentMethod] IN (0, 1)");
            table.HasCheckConstraint("CK_Orders_NonNegativeAmounts", "[SubTotal] >= 0 AND [ShippingCost] >= 0 AND [TaxAmount] >= 0 AND [TotalAmount] >= 0");
            table.HasCheckConstraint("CK_Orders_TotalAmount", "[TotalAmount] = [SubTotal] + [ShippingCost] + [TaxAmount]");
        });

        builder.HasKey(order => order.Id);
        builder.Property(order => order.OrderNumber).HasMaxLength(32).IsRequired();
        builder.Property(order => order.UserId).HasMaxLength(450).IsRequired();
        builder.Property(order => order.OrderDate).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(order => order.Status).HasConversion<int>();
        builder.Property(order => order.PaymentMethod).HasConversion<int>();
        builder.Property(order => order.SubTotal).HasPrecision(18, 2);
        builder.Property(order => order.ShippingCost).HasPrecision(18, 2);
        builder.Property(order => order.TaxAmount).HasPrecision(18, 2);
        builder.Property(order => order.TotalAmount).HasPrecision(18, 2);
        builder.Property(order => order.RecipientName).HasMaxLength(200).IsRequired();
        builder.Property(order => order.ShippingPhone).HasMaxLength(30).IsRequired();
        builder.Property(order => order.ShippingAddress).HasMaxLength(1000).IsRequired();
        builder.HasIndex(order => order.OrderNumber).IsUnique();
        builder.HasIndex(order => new { order.UserId, order.OrderDate });

        // A user with order history cannot be physically deleted through a cascade.
        builder.HasOne<ApplicationUser>()
            .WithMany(user => user.Orders)
            .HasForeignKey(order => order.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
