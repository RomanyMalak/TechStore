using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechStore.Application.Common;
using TechStore.Application.DTOs.AdminAccounts;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.DTOs.Products;
using TechStore.Infrastructure.Identity;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class AdminAccountSqlServerTests(SqlServerFixture database)
{
    private const string CreatorClaim = "techstore:audit:created-by-admin";

    [SqlServerFact]
    public async Task AdminCanCreateAdmin_NewAdminLogsInAndManagesCatalog_ListingHasSafeAuditData()
    {
        await using var factory = new SqlApiFactory(database);
        var admin = await factory.AdminAsync(); using var client = admin.Client;
        var customer = await factory.CustomerAsync(); using var customerClient = customer.Client;
        using var guest = factory.Client();
        var dto = Request();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/api/admin/admins", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customerClient.PostAsJsonAsync("/api/admin/admins", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/admin/admins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customerClient.GetAsync("/api/admin/admins")).StatusCode);
        var created = await client.PostAsJsonAsync("/api/admin/admins", dto);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(dto.Password, text);
        using (var json = JsonDocument.Parse(text))
        {
            Assert.False(json.RootElement.TryGetProperty("password", out _));
            Assert.False(json.RootElement.TryGetProperty("passwordHash", out _));
            Assert.False(json.RootElement.TryGetProperty("securityStamp", out _));
            Assert.EndsWith("Z", json.RootElement.GetProperty("createdAtUtc").GetString());
        }
        var account = JsonSerializer.Deserialize<AdminAccountResponseDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(admin.User.Id, account.CreatedByAdminId);
        using var newAdmin = factory.Client();
        var login = await newAdmin.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = dto.Email, Password = dto.Password });
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        Assert.Contains("Admin", auth.User.Roles);
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Claims, claim => claim.Type == CreatorClaim);
        newAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var categories = (await newAdmin.GetFromJsonAsync<List<CategoryResponseDto>>("/api/categories"))!;
        var product = await newAdmin.PostAsJsonAsync("/api/products", new CreateProductDto
        {
            Name = "SQL new admin product", Description = "Created by a new admin in isolated SQL tests", SKU = Guid.NewGuid().ToString("N"),
            Brand = "Test", Price = 100m, Stock = 1, CategoryId = categories.First(category => category.IsActive).Id,
            IsActive = true, Images = []
        });
        Assert.Equal(HttpStatusCode.Created, product.StatusCode);
        var list = (await newAdmin.GetFromJsonAsync<PagedResult<AdminAccountResponseDto>>("/api/admin/admins?PageSize=1&Search=" + Uri.EscapeDataString(dto.Email)))!;
        Assert.Equal(1, list.TotalCount);
        Assert.Equal(account.Id, Assert.Single(list.Items).Id);
        Assert.Equal(admin.User.Id, list.Items[0].CreatedByAdminId);
        await using var verify = database.Context();
        var role = await verify.Roles.SingleAsync(role => role.Name == "Admin");
        Assert.True(await verify.UserRoles.AnyAsync(link => link.UserId == account.Id && link.RoleId == role.Id));
        Assert.Equal(admin.User.Id, await verify.UserClaims.Where(claim => claim.UserId == account.Id && claim.ClaimType == CreatorClaim).Select(claim => claim.ClaimValue).SingleAsync());
    }

    [SqlServerFact]
    public async Task DuplicateWeakPasswordsAndPublicRoleOverposting_AreRejectedWithoutPromotion()
    {
        await using var factory = new SqlApiFactory(database);
        var admin = await factory.AdminAsync(); using var client = admin.Client;
        var dto = Request();
        (await client.PostAsJsonAsync("/api/admin/admins", dto)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/admins", dto)).StatusCode);
        var weak = Request(); weak.Password = new string('a', 12);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/admins", weak)).StatusCode);
        await using (var verify = database.Context())
            Assert.False(await verify.Users.AnyAsync(user => user.Email == weak.Email));
        var publicDto = Request();
        using var publicClient = factory.Client();
        var registration = await publicClient.PostAsJsonAsync("/api/auth/register", new
        {
            publicDto.FullName, publicDto.Email, publicDto.Password, ConfirmPassword = publicDto.Password,
            Roles = new[] { "Admin" }, Role = "Admin", CreatedByAdminId = admin.User.Id
        });
        registration.EnsureSuccessStatusCode();
        var user = (await registration.Content.ReadFromJsonAsync<UserResponseDto>())!;
        Assert.Equal(new[] { "Customer" }, user.Roles);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/admins", publicDto)).StatusCode);
        var auth = (await (await publicClient.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = publicDto.Email, Password = publicDto.Password }))
            .Content.ReadFromJsonAsync<AuthResponseDto>())!;
        Assert.Equal(new[] { "Customer" }, auth.User.Roles);
        publicClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await publicClient.GetAsync("/api/admin/admins")).StatusCode);
    }

    [SqlServerFact]
    public async Task FailedCreatorAudit_RollsBackIdentityUserAndAdminRoleTogether()
    {
        var request = Request();
        var failure = new FailCreatorAudit(request.Email);
        await using var factory = new SqlApiFactory(database, failure);
        var admin = await factory.AdminAsync(); using var client = admin.Client;
        var result = await client.PostAsJsonAsync("/api/admin/admins", request);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.False(string.IsNullOrEmpty(failure.FailedUserId));
        Assert.DoesNotContain(request.Password, await result.Content.ReadAsStringAsync());
        await using var verify = database.Context();
        Assert.False(await verify.Users.AnyAsync(user => user.Id == failure.FailedUserId || user.Email == request.Email));
        Assert.False(await verify.UserRoles.AnyAsync(link => link.UserId == failure.FailedUserId));
        Assert.False(await verify.UserClaims.AnyAsync(claim => claim.UserId == failure.FailedUserId));
    }

    private static CreateAdminDto Request() => new()
    {
        FullName = "Isolated admin", Email = Guid.NewGuid().ToString("N") + "@isolated.example.test", Password = Guid.NewGuid().ToString("N") + "Aa1!"
    };

    private sealed class InjectedAuditFailure : Exception;
    private sealed class FailCreatorAudit(string email) : SaveChangesInterceptor
    {
        public string? FailedUserId { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            var context = data.Context!;
            if (context.ChangeTracker.Entries<ApplicationUser>().Any(entry => entry.Entity.Email == email))
            {
                var claim = context.ChangeTracker.Entries<IdentityUserClaim<string>>().FirstOrDefault(entry => entry.State == EntityState.Added && entry.Entity.ClaimType == CreatorClaim);
                if (claim is not null) { FailedUserId = claim.Entity.UserId; throw new InjectedAuditFailure(); }
            }
            return ValueTask.FromResult(result);
        }
    }
}
