using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Services;
using TechStore.Infrastructure.Persistence;
using ApiProgram = TechStore.API.Program;

namespace TechStore.API.Tests;

public class ApiFactory : WebApplicationFactory<ApiProgram>
{
    private readonly string _environment;
    private readonly bool _configureAdmin;
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    // Reuse EF's provider root while keeping each factory's database name unique.
    // Creating a root per factory needlessly creates an internal provider per host.
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    public string AdminPassword { get; } = Guid.NewGuid().ToString("N") + "Aa1!";
    public const string AdminEmail = "admin@test.techstore.local";
    public Mock<IProductService> Products { get; } = new(MockBehavior.Strict);
    public Mock<ICategoryService> Categories { get; } = new(MockBehavior.Strict);
    public static byte[] Version => new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 };

    public ApiFactory() : this("Development", true) { }

    internal ApiFactory(string environment, bool configureAdmin = true)
    {
        _environment = environment;
        _configureAdmin = configureAdmin;
        Products.Setup(service => service.GetProductsAsync(It.IsAny<ProductQueryDto>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<ProductListDto>(new[] { new ProductListDto { Id = 1, Name = "Laptop" } }, 1, 1, 20));
        Products.Setup(service => service.GetProductByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Product());
        Products.Setup(service => service.CreateProductAsync(It.IsAny<CreateProductDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Product());
        Products.Setup(service => service.UpdateProductAsync(It.IsAny<int>(), It.IsAny<UpdateProductDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Product());
        Products.Setup(service => service.UpdateStockAsync(It.IsAny<int>(), It.IsAny<UpdateStockDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Product());
        Products.Setup(service => service.DeleteProductAsync(It.IsAny<int>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Products.Setup(service => service.GetProductByIdAsync(404, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("Product", 404));
        Products.Setup(service => service.GetProductByIdAsync(500, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Sensitive internal implementation detail"));
        Products.Setup(service => service.GetProductsAsync(It.Is<ProductQueryDto>(query => query.PageSize == 0), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("PageSize", "Page size must be positive."));
        Products.Setup(service => service.CreateProductAsync(It.Is<CreateProductDto>(dto => dto.SKU == "DUPLICATE"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("SKU already exists."));
        Products.Setup(service => service.CreateProductAsync(It.Is<CreateProductDto>(dto => dto.CategoryId == 404), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("Category", 404));
        Products.Setup(service => service.UpdateProductAsync(It.IsAny<int>(), It.Is<UpdateProductDto>(dto => dto.SKU == "STALE"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("The product changed."));
        Products.Setup(service => service.UpdateProductAsync(It.IsAny<int>(), It.Is<UpdateProductDto>(dto => dto.SKU == "EF-CONCURRENCY"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("Internal EF details"));
        Categories.Setup(service => service.GetCategoriesAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Category() });
        Categories.Setup(service => service.GetCategoryByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Category());
        Categories.Setup(service => service.CreateCategoryAsync(It.IsAny<CreateCategoryDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Category());
        Categories.Setup(service => service.UpdateCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateCategoryDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Category());
        Categories.Setup(service => service.DeactivateCategoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "TechStore.API.Tests", ["Jwt:Audience"] = "TechStore.Tests.Client",
            ["Jwt:SigningKey"] = SigningKey, ["Jwt:ExpirationMinutes"] = "60",
            ["DevelopmentAdmin:Email"] = _configureAdmin ? AdminEmail : null,
            ["DevelopmentAdmin:Password"] = _configureAdmin ? AdminPassword : null
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName, DatabaseRoot)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.RemoveAll<IProductService>();
            services.RemoveAll<ICategoryService>();
            services.AddScoped(_ => Products.Object);
            services.AddScoped(_ => Categories.Object);
        });
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    public async Task<HttpClient> AdminClientAsync()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = AdminEmail, Password = AdminPassword });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    public async Task<HttpClient> CustomerClientAsync()
    {
        var client = Client();
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        var email = Guid.NewGuid().ToString("N") + "@example.com";
        var registration = await client.PostAsJsonAsync("/api/auth/register", new RegisterDto
        {
            FullName = "Test Customer", Email = email, Password = password, ConfirmPassword = password
        });
        registration.EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    public static ProductResponseDto Product() => new() { Id = 1, Name = "Laptop", SKU = "SKU-1", Price = 100, CategoryId = 1, CategoryName = "Electronics", RowVersion = Version };
    public static CategoryResponseDto Category() => new() { Id = 1, Name = "Electronics", IsActive = true };
}
