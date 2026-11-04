using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Identity;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("Addresses");
        builder.HasKey(address => address.Id);
        builder.Property(address => address.UserId).HasMaxLength(450).IsRequired();
        builder.Property(address => address.FullName).HasMaxLength(200).IsRequired();
        builder.Property(address => address.Phone).HasMaxLength(30).IsRequired();
        builder.Property(address => address.City).HasMaxLength(100).IsRequired();
        builder.Property(address => address.Street).HasMaxLength(300).IsRequired();
        builder.Property(address => address.Building).HasMaxLength(100).IsRequired();
        builder.HasIndex(address => address.UserId)
            .HasDatabaseName("IX_Addresses_OneDefaultPerUser")
            .IsUnique()
            .HasFilter("[IsDefault] = 1");

        builder.HasOne<ApplicationUser>()
            .WithMany(user => user.Addresses)
            .HasForeignKey(address => address.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
