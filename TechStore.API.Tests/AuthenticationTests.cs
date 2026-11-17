using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Auth;
using TechStore.Infrastructure.Identity;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests;

public class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Registration_AlwaysAssignsCustomer_EvenWhenAdminRoleIsSubmitted()
    {
        using var client = factory.Client();
        var request = Registration();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            request.FullName, request.Email, request.Password, request.ConfirmPassword,
            role = Roles.Admin, roles = new[] { Roles.Admin }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var user = JsonSerializer.Deserialize<UserResponseDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(Roles.Customer, Assert.Single(user.Roles));
        Assert.DoesNotContain(request.Password, text);
        Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.False(await manager.IsInRoleAsync((await manager.FindByIdAsync(user.Id))!, Roles.Admin));
    }

    [Fact]
    public async Task CustomerRegistrationAndLogin_ProduceSubjectAndCustomerRoleClaims()
    {
        using var client = factory.Client();
        var registration = Registration();
        var created = await client.PostAsJsonAsync("/api/auth/register", registration);
        var user = (await created.Content.ReadFromJsonAsync<UserResponseDto>())!;
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = registration.Email, Password = registration.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        Assert.Equal(user.Id, jwt.Subject);
        Assert.Equal(Roles.Customer, Assert.Single(jwt.Claims, claim => claim.Type == "role").Value);
        Assert.Equal("Bearer", auth.TokenType);
        Assert.True(auth.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task SeededAdmin_LoginContainsAdminRoleAndUserId()
    {
        using var client = factory.Client();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = ApiFactory.AdminEmail, Password = factory.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        Assert.Equal(auth.User.Id, jwt.Subject);
        Assert.Contains(jwt.Claims, claim => claim.Type == "role" && claim.Value == Roles.Admin);
    }

    [Fact]
    public async Task DuplicateRegistration_Returns409()
    {
        using var client = factory.Client();
        var registration = Registration();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", registration)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", registration)).StatusCode);
    }

    [Fact]
    public async Task WeakPassword_Returns400WithIdentityValidationErrors()
    {
        using var client = factory.Client();
        var registration = Registration();
        registration.Password = new string('a', 8);
        registration.ConfirmPassword = registration.Password;
        var response = await client.PostAsJsonAsync("/api/auth/register", registration);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Password", out _));
    }

    [Fact]
    public async Task InvalidCredentials_Return401WithoutTokenOrInternalDetails()
    {
        using var client = factory.Client();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Email = Guid.NewGuid().ToString("N") + "@example.com", Password = Guid.NewGuid().ToString("N")
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", text);
        Assert.DoesNotContain("stackTrace", text);
    }

    [Fact]
    public async Task RepeatedWrongPasswords_LockTheAccount()
    {
        using var client = factory.Client();
        var registration = Registration();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", registration)).StatusCode);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
            {
                Email = registration.Email, Password = Guid.NewGuid().ToString("N")
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = registration.Email, Password = registration.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task SeedingTwice_DoesNotDuplicateAdminOrRoles()
    {
        using var client = factory.Client();
        using var scope = factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
        await seeder.SeedRolesAsync();
        await seeder.SeedDevelopmentAdminAsync();
        await seeder.SeedDevelopmentAdminAsync();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await context.Users.CountAsync(user => user.Email == ApiFactory.AdminEmail));
        Assert.Equal(1, await context.Roles.CountAsync(role => role.Name == Roles.Admin));
        Assert.Equal(1, await context.Roles.CountAsync(role => role.Name == Roles.Customer));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    public async Task InvalidJwt_CannotAccessAdminEndpoint(string reason)
    {
        using var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", InvalidToken(reason));
        using var request = CatalogRequests.Management("POST", "/api/products");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    private string InvalidToken(string reason)
    {
        var key = reason == "signature" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) : factory.SigningKey;
        var credentials = reason == "unsigned" ? null : new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            reason == "algorithm" ? SecurityAlgorithms.HmacSha512 : SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(reason == "issuer" ? "wrong-issuer" : "TechStore.API.Tests",
            reason == "audience" ? "wrong-audience" : "TechStore.Tests.Client",
            new[] { new Claim("sub", "test-subject"), new Claim("role", Roles.Admin) },
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(reason == "expired" ? -5 : 5), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal static RegisterDto Registration()
    {
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        return new RegisterDto
        {
            FullName = "Test Customer", Email = Guid.NewGuid().ToString("N") + "@example.com",
            Password = password, ConfirmPassword = password
        };
    }
}
