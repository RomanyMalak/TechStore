using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TechStore.Application.Common;
using TechStore.Application.DTOs.AdminAccounts;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Tests;

public class AdminAccountEndpointTests(AdminAccountEndpointTests.AccountFactory factory)
    : IClassFixture<AdminAccountEndpointTests.AccountFactory>
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Guest_CannotListOrCreateAdmins_AndServiceIsNotCalled(string method)
    {
        using var client = factory.Client();
        var before = factory.Accounts.Invocations.Count;
        using var request = Request(method);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal(before, factory.Accounts.Invocations.Count);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Customer_CannotListOrCreateAdmins_AndServiceIsNotCalled(string method)
    {
        using var client = await factory.CustomerClientAsync();
        var before = factory.Accounts.Invocations.Count;
        using var request = Request(method);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, factory.Accounts.Invocations.Count);
    }

    [Fact]
    public async Task AdminList_BindsPaginationAndSearch_ReturnsOnlySafeFields_WithNoStore()
    {
        using var client = await factory.AdminClientAsync();
        var response = await client.GetAsync("/api/admin/admins?PageNumber=2&PageSize=7&Search=Support");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        AssertSafe(body);
        var page = (await response.Content.ReadFromJsonAsync<PagedResult<AdminAccountResponseDto>>())!;
        Assert.Single(page.Items);
        factory.Accounts.Verify(service => service.GetAdminsAsync(It.Is<AdminAccountQueryDto>(query =>
            query.PageNumber == 2 && query.PageSize == 7 && query.Search == "Support"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdminCreation_UsesJwtActor_IgnoresSubmittedRoles_AndNeverReturnsPassword()
    {
        using var client = await factory.AdminClientAsync();
        var actorId = new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter).Subject;
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        var response = await client.PostAsJsonAsync("/api/admin/admins", new
        {
            fullName = "Support Admin", email = "new-admin@example.test", password,
            roles = new[] { "SuperAdmin" }, role = "Customer", createdByAdminId = "forged-actor"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        AssertSafe(body);
        Assert.DoesNotContain(password, body);
        Assert.Equal(actorId, (await response.Content.ReadFromJsonAsync<AdminAccountResponseDto>())!.CreatedByAdminId);
        factory.Accounts.Verify(service => service.CreateAdminAsync(actorId, It.Is<CreateAdminDto>(dto =>
            dto.FullName == "Support Admin" && dto.Email == "new-admin@example.test" && dto.Password == password),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DuplicateEmail_Return409_AndIdentityValidationErrorsReturn400()
    {
        using var client = await factory.AdminClientAsync();
        var valid = new { fullName = "Admin", email = "duplicate@example.test", password = Guid.NewGuid().ToString("N") + "Aa1!" };
        var duplicate = await client.PostAsJsonAsync("/api/admin/admins", valid);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var weak = await client.PostAsJsonAsync("/api/admin/admins", new
        { fullName = "Admin", email = "weak@example.test", password = "lowercaseonly" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var body = await weak.Content.ReadAsStringAsync();
        Assert.DoesNotContain("lowercaseonly", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Password", out _));
    }

    [Fact]
    public async Task MissingRequiredFields_Return400BeforeService()
    {
        using var client = await factory.AdminClientAsync();
        var before = factory.Accounts.Invocations.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/admins", new { })).StatusCode);
        Assert.Equal(before, factory.Accounts.Invocations.Count);
    }

    [Fact]
    public async Task ServiceQueryValidation_Return400AndActualActorRejection_Return403()
    {
        using var client = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/admins?PageSize=0")).StatusCode);
        var response = await client.PostAsJsonAsync("/api/admin/admins", new
        { fullName = "Admin", email = "forbidden@example.test", password = Guid.NewGuid().ToString("N") + "Aa1!" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Swagger_ExposesNoIdentitySecrets_AndPublicRegistrationHasNoRoleAssignment()
    {
        using var client = factory.Client();
        using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = swagger.RootElement;
        var path = root.GetProperty("paths").GetProperty("/api/admin/admins");
        Assert.True(path.GetProperty("get").GetProperty("security")[0].TryGetProperty("Bearer", out _));
        var create = path.GetProperty("post");
        Assert.True(create.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("429", out _));
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var response = schemas.GetProperty("AdminAccountResponseDto");
        AssertSafe(response.GetRawText());
        var requestNames = schemas.GetProperty("CreateAdminDto").GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "email", "fullName", "password" }, requestNames.Order().ToArray());
        var register = schemas.GetProperty("RegisterDto").GetProperty("properties");
        Assert.False(register.TryGetProperty("role", out _));
        Assert.False(register.TryGetProperty("roles", out _));
    }

    private static void AssertSafe(string json)
    {
        foreach (var field in new[] { "password", "passwordHash", "securityStamp", "concurrencyStamp", "accessToken", "refreshToken" })
            Assert.DoesNotContain('"' + field + '"', json, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Request(string method) => new(new HttpMethod(method), "/api/admin/admins")
    {
        Content = method == "POST" ? JsonContent.Create(new
        { fullName = "Not authorized", email = "unused@example.test", password = Guid.NewGuid().ToString("N") + "Aa1!" }) : null
    };

    public sealed class AccountFactory : ApiFactory
    {
        public Mock<IAdminAccountService> Accounts { get; } = new(MockBehavior.Strict);

        public AccountFactory()
        {
            Accounts.Setup(service => service.GetAdminsAsync(It.IsAny<AdminAccountQueryDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AdminAccountQueryDto query, CancellationToken _) => new PagedResult<AdminAccountResponseDto>(
                    new[] { Account() }, 1, query.PageNumber, query.PageSize));
            Accounts.Setup(service => service.GetAdminsAsync(It.Is<AdminAccountQueryDto>(query => query.PageSize == 0), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ValidationException("PageSize", "Page size must be positive."));
            Accounts.Setup(service => service.CreateAdminAsync(It.IsAny<string>(), It.IsAny<CreateAdminDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string actor, CreateAdminDto _, CancellationToken _) => Account(actor));
            Accounts.Setup(service => service.CreateAdminAsync(It.IsAny<string>(), It.Is<CreateAdminDto>(dto => dto.Email == "duplicate@example.test"), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConflictException("An account with that email already exists."));
            Accounts.Setup(service => service.CreateAdminAsync(It.IsAny<string>(), It.Is<CreateAdminDto>(dto => dto.Email == "weak@example.test"), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ValidationException("Password", "Password does not meet the Identity policy."));
            Accounts.Setup(service => service.CreateAdminAsync(It.IsAny<string>(), It.Is<CreateAdminDto>(dto => dto.Email == "forbidden@example.test"), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ForbiddenException());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAdminAccountService>();
                services.AddScoped(_ => Accounts.Object);
            });
        }

        private static AdminAccountResponseDto Account(string? actor = null) => new()
        {
            Id = "test-admin", FullName = "Support Admin", Email = "admin@example.test",
            CreatedAtUtc = DateTime.UtcNow, CreatedByAdminId = actor
        };
    }
}
