using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechStore.Application.Common.Security;
using TechStore.Infrastructure.Persistence;

namespace TechStore.Infrastructure.Identity;

public sealed class IdentitySeeder(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    ApplicationDbContext context,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger)
{
    public async Task SeedRolesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var role in new[] { Roles.Admin, Roles.Customer })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded && !await roles.RoleExistsAsync(role)) EnsureSucceeded(result);
            }
        }
    }

    // The API calls this method only when its host environment is Development.
    public async Task SeedDevelopmentAdminAsync(CancellationToken cancellationToken = default)
    {
        var email = configuration["DevelopmentAdmin:Email"]?.Trim();
        var password = configuration["DevelopmentAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Development admin is not configured. Set DevelopmentAdmin:Email and DevelopmentAdmin:Password in API User Secrets.");
            return;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new ApplicationUser { UserName = email, Email = email, FullName = "Development Admin", EmailConfirmed = true };
            EnsureSucceeded(await users.CreateAsync(admin, password));
        }
        if (!await users.IsInRoleAsync(admin, Roles.Admin))
        {
            // An existing public registration must not gain Admin just by reserving the configured email.
            if (!await users.CheckPasswordAsync(admin, password))
                throw new InvalidOperationException("The configured development admin email belongs to an existing account with different credentials.");
            EnsureSucceeded(await users.AddToRoleAsync(admin, Roles.Admin));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Identity initialization failed: " + string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
