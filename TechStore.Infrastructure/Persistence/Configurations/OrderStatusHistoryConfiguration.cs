using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Identity;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("OrderStatusHistories", table =>
        {
            table.HasCheckConstraint("CK_OrderStatusHistories_OldStatus", "[OldStatus] IS NULL OR [OldStatus] IN (0, 1, 2, 3, 4, 5, 6)");
            table.HasCheckConstraint("CK_OrderStatusHistories_NewStatus", "[NewStatus] IN (0, 1, 2, 3, 4, 5, 6)");
        });

        builder.HasKey(history => history.Id);
        builder.Property(history => history.OldStatus).HasConversion<int>();
        builder.Property(history => history.NewStatus).HasConversion<int>();
        builder.Property(history => history.ChangedByUserId).HasMaxLength(450);
        builder.Property(history => history.ChangedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(history => history.Note).HasMaxLength(1000);
        builder.HasIndex(history => new { history.OrderId, history.ChangedAt });

        builder.HasOne(history => history.Order)
            .WithMany(order => order.StatusHistory)
            .HasForeignKey(history => history.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // No cascade from the actor: deleting an account must not erase audit entries.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(history => history.ChangedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
