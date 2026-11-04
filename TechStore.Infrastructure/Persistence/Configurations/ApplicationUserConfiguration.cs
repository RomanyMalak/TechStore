using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechStore.Infrastructure.Identity;

namespace TechStore.Infrastructure.Persistence.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.FullName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.PhoneNumber).HasMaxLength(30);
        builder.Property(user => user.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
    }
}
