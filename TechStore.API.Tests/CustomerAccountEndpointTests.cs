using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TechStore.Application.DTOs.Addresses;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.CustomerAccounts;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Tests;

public class CustomerAccountEndpointTests(CustomerAccountEndpointTests.AccountFactory factory)
    : IClassFixture<CustomerAccountEndpointTests.AccountFactory>
{
    public static IEnumerable<object[]> Operations =>
    [
        ["GET", "/api/account/profile"], ["PUT", "/api/account/profile"],
        ["GET", "/api/account/addresses"], ["GET", "/api/account/addresses/17"],
        ["POST", "/api/account/addresses"], ["PUT", "/api/account/addresses/17"],
        ["DELETE", "/api/account/addresses/17"], ["PUT", "/api/account/addresses/17/default"]
    ];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Guest_CannotAccessAnyAccountOperation_OrInvokeTheService(string method, string route)
    {
        using var client = factory.Client();
        var before = factory.Account.Invocations.Count;
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "PUT" or "POST") request.Content = JsonContent.Create(AddressRequest());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal(before, factory.Account.Invocations.Count);
    }

    [Fact]
    public async Task ProfileReadAndUpdate_UseJwtIdentity_AndIgnoreEmailRoleOrUserIdOverposting()
    {
        using var client = await factory.CustomerClientAsync();
        var owner = Subject(client);
        var profile = await client.GetAsync("/api/account/profile?UserId=other");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.True(profile.Headers.CacheControl?.NoStore);
        Assert.Equal(owner, (await profile.Content.ReadFromJsonAsync<UserResponseDto>())!.Id);
        var update = await client.PutAsJsonAsync("/api/account/profile", new
        {
            fullName = "Updated profile", phoneNumber = "01012345678", userId = "other",
            email = "forged@example.test", roles = new[] { "Admin" }, password = "Ignored fake value"
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.True(update.Headers.CacheControl?.NoStore);
        var user = (await update.Content.ReadFromJsonAsync<UserResponseDto>())!;
        Assert.Equal(owner, user.Id);
        Assert.Equal("original@example.test", user.Email);
        Assert.Equal("Customer", Assert.Single(user.Roles));
        AssertSecretFree(await update.Content.ReadAsStringAsync());
        factory.Account.Verify(service => service.UpdateProfileAsync(owner,
            It.Is<UpdateProfileDto>(dto => dto.FullName == "Updated profile" && dto.PhoneNumber == "01012345678"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Admin_CanUseTheirOwnProfileWithoutCustomerRoleRequirement()
    {
        using var client = await factory.AdminClientAsync();
        var response = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        factory.Account.Verify(service => service.GetProfileAsync(Subject(client), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddressReads_ArePrivateAndScopedByJwt()
    {
        using var client = await factory.CustomerClientAsync();
        var owner = Subject(client);
        var list = await client.GetAsync("/api/account/addresses?UserId=other");
        Assert.True(list.Headers.CacheControl?.NoStore);
        Assert.Single((await list.Content.ReadFromJsonAsync<AddressResponseDto[]>())!);
        var single = await client.GetAsync("/api/account/addresses/17?UserId=other");
        Assert.True(single.Headers.CacheControl?.NoStore);
        Assert.Equal(17, (await single.Content.ReadFromJsonAsync<AddressResponseDto>())!.Id);
        factory.Account.Verify(service => service.GetAddressesAsync(owner, It.IsAny<CancellationToken>()), Times.Once);
        factory.Account.Verify(service => service.GetAddressAsync(owner, 17, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddressCreation_Returns201AndValidLocation_WithoutAcceptingAnOwnerFromJson()
    {
        using var client = await factory.CustomerClientAsync();
        var owner = Subject(client);
        var response = await client.PostAsJsonAsync("/api/account/addresses", new
        {
            fullName = "Recipient", phone = "01012345678", city = "Cairo", street = "API street", building = "10",
            isDefault = true, userId = "someone-else"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.EndsWith("/api/account/addresses/17", response.Headers.Location?.ToString());
        Assert.True((await response.Content.ReadFromJsonAsync<AddressResponseDto>())!.IsDefault);
        factory.Account.Verify(service => service.CreateAddressAsync(owner,
            It.Is<CreateAddressDto>(dto => dto.FullName == "Recipient" && dto.IsDefault && dto.City == "Cairo"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDeleteAndBodylessDefault_AlwaysUseJwtOwner()
    {
        using var client = await factory.CustomerClientAsync();
        var owner = Subject(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/account/addresses/17?UserId=other", AddressRequest())).StatusCode);
        var selected = await client.PutAsJsonAsync("/api/account/addresses/17/default", new { userId = "other" });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.True(selected.Headers.CacheControl?.NoStore);
        var deleted = await client.DeleteAsync("/api/account/addresses/17?UserId=other");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.True(deleted.Headers.CacheControl?.NoStore);
        factory.Account.Verify(service => service.UpdateAddressAsync(owner, 17, It.IsAny<UpdateAddressDto>(), It.IsAny<CancellationToken>()), Times.Once);
        factory.Account.Verify(service => service.SetDefaultAddressAsync(owner, 17, It.IsAny<CancellationToken>()), Times.Once);
        factory.Account.Verify(service => service.DeleteAddressAsync(owner, 17, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("GET", "/api/account/addresses/404")]
    [InlineData("PUT", "/api/account/addresses/404")]
    [InlineData("DELETE", "/api/account/addresses/404")]
    [InlineData("PUT", "/api/account/addresses/404/default")]
    public async Task OwnershipFilteredServiceFailures_Return404ForForeignOrMissingAddresses(string method, string route)
    {
        using var client = await factory.CustomerClientAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "PUT") request.Content = JsonContent.Create(AddressRequest());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RequiredFieldsAndApplicationValidation_Return400_ConcurrencyReturns409()
    {
        using var client = await factory.CustomerClientAsync();
        var before = factory.Account.Invocations.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/account/profile", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/addresses", new { })).StatusCode);
        Assert.Equal(before, factory.Account.Invocations.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/account/profile", new { fullName = "INVALID" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("/api/account/addresses/409/default", content: null)).StatusCode);
    }

    [Fact]
    public async Task Swagger_DocumentsAccountRoutesAndSafeRestrictedDtos()
    {
        using var client = factory.Client();
        using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = swagger.RootElement;
        foreach (var path in new[] { "/api/account/profile", "/api/account/addresses", "/api/account/addresses/{id}", "/api/account/addresses/{id}/default" })
        {
            foreach (var operation in root.GetProperty("paths").GetProperty(path).EnumerateObject())
            {
                Assert.True(operation.Value.GetProperty("security")[0].TryGetProperty("Bearer", out _));
                if (operation.Name is "put" or "post" or "delete")
                    Assert.True(operation.Value.GetProperty("responses").TryGetProperty("429", out _));
            }
        }
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var fields = schemas.GetProperty("UpdateProfileDto").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "fullName", "phoneNumber" }, fields);
        Assert.False(schemas.GetProperty("CreateAddressDto").GetProperty("properties").TryGetProperty("userId", out _));
        AssertSecretFree(schemas.GetProperty("UserResponseDto").GetRawText());
        AssertSecretFree(schemas.GetProperty("AddressResponseDto").GetRawText());
    }

    private static string Subject(HttpClient client) => new JwtSecurityTokenHandler()
        .ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter).Subject;
    private static CreateAddressDto AddressRequest() => new()
    { FullName = "Recipient", Phone = "01012345678", City = "Cairo", Street = "API street", Building = "10", IsDefault = true };
    private static void AssertSecretFree(string json)
    {
        foreach (var field in new[] { "password", "passwordHash", "securityStamp", "concurrencyStamp", "accessToken", "refreshToken" })
            Assert.DoesNotContain('"' + field + '"', json, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class AccountFactory : ApiFactory
    {
        public Mock<ICustomerAccountService> Account { get; } = new(MockBehavior.Strict);
        public AccountFactory()
        {
            Account.Setup(service => service.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string owner, CancellationToken _) => Profile(owner));
            Account.Setup(service => service.UpdateProfileAsync(It.IsAny<string>(), It.IsAny<UpdateProfileDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string owner, UpdateProfileDto dto, CancellationToken _) => Profile(owner, dto.FullName, dto.PhoneNumber));
            Account.Setup(service => service.UpdateProfileAsync(It.IsAny<string>(), It.Is<UpdateProfileDto>(dto => dto.FullName == "INVALID"), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ValidationException("FullName", "Invalid profile name."));
            Account.Setup(service => service.GetAddressesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Address() });
            Account.Setup(service => service.GetAddressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Address());
            Account.Setup(service => service.CreateAddressAsync(It.IsAny<string>(), It.IsAny<CreateAddressDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Address());
            Account.Setup(service => service.UpdateAddressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateAddressDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Address());
            Account.Setup(service => service.DeleteAddressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Account.Setup(service => service.SetDefaultAddressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Address());
            Account.Setup(service => service.GetAddressAsync(It.IsAny<string>(), 404, It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("Address", 404));
            Account.Setup(service => service.UpdateAddressAsync(It.IsAny<string>(), 404, It.IsAny<UpdateAddressDto>(), It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("Address", 404));
            Account.Setup(service => service.DeleteAddressAsync(It.IsAny<string>(), 404, It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("Address", 404));
            Account.Setup(service => service.SetDefaultAddressAsync(It.IsAny<string>(), 404, It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("Address", 404));
            Account.Setup(service => service.SetDefaultAddressAsync(It.IsAny<string>(), 409, It.IsAny<CancellationToken>())).ThrowsAsync(new ConflictException("The address changed concurrently."));
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICustomerAccountService>();
                services.AddScoped(_ => Account.Object);
            });
        }
        private static UserResponseDto Profile(string id, string name = "Current Customer", string? phone = null) => new()
        { Id = id, FullName = name, PhoneNumber = phone, Email = "original@example.test", CreatedAt = DateTime.UtcNow, Roles = ["Customer"] };
        private static AddressResponseDto Address() => new()
        { Id = 17, FullName = "Recipient", Phone = "01012345678", City = "Cairo", Street = "API street", Building = "10", IsDefault = true };
    }
}
