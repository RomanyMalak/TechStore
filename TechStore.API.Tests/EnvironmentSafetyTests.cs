using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechStore.Application.Common.Security;
using TechStore.Infrastructure.Identity;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests;

public class EnvironmentSafetyTests
{
    [Fact]
    public async Task Production_DoesNotSeedDevelopmentAdminOrExposeSwagger()
    {
        using var factory = new ApiFactory("Production");
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await context.Users.AnyAsync(user => user.Email == ApiFactory.AdminEmail));
        Assert.True(await context.Roles.AnyAsync(role => role.Name == Roles.Customer));
        var error = await client.GetStringAsync("/api/categories");
        Assert.DoesNotContain(factory.AdminPassword, error);
    }

    [Fact]
    public async Task MissingDevelopmentCredentials_DoesNotCreateAnAdmin()
    {
        using var factory = new ApiFactory("Development", configureAdmin: false);
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/categories")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await context.Users.ToListAsync());
    }

    [Fact]
    public async Task ExistingPublicAccount_IsNotPromotedByReservingAdminEmail()
    {
        using var factory = new ApiFactory("Development", configureAdmin: false);
        using var client = factory.Client();
        var request = AuthenticationTests.Registration();
        request.Email = ApiFactory.AdminEmail;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        configuration["DevelopmentAdmin:Email"] = ApiFactory.AdminEmail;
        configuration["DevelopmentAdmin:Password"] = factory.AdminPassword;
        var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedDevelopmentAdminAsync());
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByEmailAsync(ApiFactory.AdminEmail))!;
        Assert.False(await manager.IsInRoleAsync(user, Roles.Admin));
    }

    [Fact]
    public async Task Production_UnexpectedErrorHasNoInternalDetails()
    {
        using var factory = new ApiFactory("Production");
        using var client = factory.Client();
        var response = await client.GetAsync("/api/products/500");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Sensitive internal", await response.Content.ReadAsStringAsync());
    }
}
