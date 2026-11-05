using Moq;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Services;
using TechStore.Application.Validators.Products;
using TechStore.Domain.Entities;

namespace TechStore.Application.Tests.Services;

public class ProductServiceTests
{
    private readonly Mock<IProductRepository> _products = new(MockBehavior.Strict);
    private readonly Mock<ICategoryRepository> _categories = new(MockBehavior.Strict);
    private readonly Category _category = new() { Id = 1, Name = "Laptops" };
    private readonly Product _existing;
    private readonly ProductService _service;

    public ProductServiceTests()
    {
        _existing = Product.Create("Laptop", "Existing description", "SKU-1", "Dell", 100, null, 5, 1);
        _existing.Id = 7;
        _existing.Category = _category;
        _existing.RowVersion = Version(1);
        _products.Setup(repository => repository.ExistsBySkuAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _products.Setup(repository => repository.GetForUpdateAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(_existing);
        _products.Setup(repository => repository.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()))
            .Callback<Product, CancellationToken>((product, _) => { product.Id = 42; product.RowVersion = Version(2); })
            .Returns(Task.CompletedTask);
        _products.Setup(repository => repository.Update(It.IsAny<Product>(), It.IsAny<byte[]>()));
        _products.Setup(repository => repository.SoftDelete(It.IsAny<Product>(), It.IsAny<byte[]>()));
        _products.Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _categories.Setup(repository => repository.GetByIdAsync(1, true, It.IsAny<CancellationToken>())).ReturnsAsync(_category);
        _service = new ProductService(_products.Object, _categories.Object, new CreateProductDtoValidator(),
            new UpdateProductDtoValidator(), new ProductQueryDtoValidator(), new UpdateStockDtoValidator());
    }

    [Fact]
    public async Task CreateValidProduct_NormalizesPersistsAndMapsWithoutAnotherQuery()
    {
        var dto = ValidCreate();
        dto.Name = "  New laptop  ";
        dto.SKU = "  NEW-SKU  ";
        dto.Images.Add(new ProductImageRequestDto { ImageUrl = "https://example.com/main.png", IsPrimary = true });
        var response = await _service.CreateProductAsync(dto);
        Assert.Equal(42, response.Id);
        Assert.Equal("New laptop", response.Name);
        Assert.Equal("NEW-SKU", response.SKU);
        Assert.Equal("Laptops", response.CategoryName);
        Assert.Equal(90, response.DiscountPrice);
        Assert.Single(response.Images);
        Assert.Equal(Version(2), response.RowVersion);
        _products.Verify(repository => repository.AddAsync(It.Is<Product>(product => product.Price == 100 && product.Stock == 5), It.IsAny<CancellationToken>()), Times.Once);
        _products.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _products.Verify(repository => repository.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateDuplicateSku_ThrowsConflictWithoutPersisting()
    {
        _products.Setup(repository => repository.ExistsBySkuAsync("NEW-SKU", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateProductAsync(ValidCreate()));
        VerifyNoWrites();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateNonPositivePrice_RejectsBeforeDataAccess(decimal price)
    {
        var dto = ValidCreate(); dto.Price = price;
        var error = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        Assert.Contains(nameof(dto.Price), error.Errors.Keys);
        Assert.Empty(_products.Invocations);
        Assert.Empty(_categories.Invocations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(101)]
    public async Task CreateInvalidDiscount_IsRejected(decimal discount)
    {
        var dto = ValidCreate(); dto.DiscountPrice = discount;
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        VerifyNoWrites();
    }

    [Fact]
    public async Task CreateFractionalCentPrice_IsRejected()
    {
        var dto = ValidCreate(); dto.Price = 100.001m;
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        VerifyNoWrites();
    }

    [Fact]
    public async Task CreateNegativeStock_IsRejected()
    {
        var dto = ValidCreate(); dto.Stock = -1;
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        VerifyNoWrites();
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("SKU")]
    [InlineData("Description")]
    [InlineData("Brand")]
    [InlineData("CategoryId")]
    public async Task CreateInvalidField_IsRejected(string field)
    {
        var dto = ValidCreate();
        switch (field)
        {
            case "Name": dto.Name = new string('N', 201); break;
            case "SKU": dto.SKU = new string('S', 51); break;
            case "Description": dto.Description = " "; break;
            case "Brand": dto.Brand = ""; break;
            case "CategoryId": dto.CategoryId = 0; break;
        }
        var error = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        Assert.Contains(field, error.Errors.Keys);
        VerifyNoWrites();
    }

    [Fact]
    public async Task CreateMissingCategory_ThrowsNotFound()
    {
        _categories.Setup(repository => repository.GetByIdAsync(1, true, It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);
        await Assert.ThrowsAsync<NotFoundException>(() => _service.CreateProductAsync(ValidCreate()));
        VerifyNoWrites();
    }

    [Fact]
    public async Task CreateInactiveCategory_IsRejected()
    {
        _category.Deactivate();
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(ValidCreate()));
        VerifyNoWrites();
    }

    [Fact]
    public async Task CreateMultiplePrimaryImages_IsRejected()
    {
        var dto = ValidCreate();
        dto.Images = new()
        {
            new() { ImageUrl = "https://example.com/one.png", IsPrimary = true },
            new() { ImageUrl = "https://example.com/two.png", IsPrimary = true }
        };
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        VerifyNoWrites();
    }

    [Theory]
    [InlineData("file:///image.png")]
    [InlineData("not-a-url")]
    public async Task CreateInvalidImageUrl_IsRejected(string url)
    {
        var dto = ValidCreate(); dto.Images.Add(new() { ImageUrl = url });
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(dto));
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateExistingProduct_ExcludesOwnSkuAndReplacesImages()
    {
        _existing.Images.Add(new() { Id = 1, ImageUrl = "https://example.com/old.png", IsPrimary = true });
        var dto = ValidUpdate();
        dto.Images.Add(new() { ImageUrl = "https://example.com/new.png", IsPrimary = true });
        var response = await _service.UpdateProductAsync(7, dto);
        Assert.Equal("Updated laptop", response.Name);
        Assert.Equal(120, _existing.Price);
        Assert.NotNull(_existing.UpdatedAt);
        Assert.Equal("https://example.com/new.png", Assert.Single(response.Images).ImageUrl);
        _products.Verify(repository => repository.ExistsBySkuAsync("SKU-1", 7, It.IsAny<CancellationToken>()), Times.Once);
        _products.Verify(repository => repository.Update(_existing, dto.RowVersion), Times.Once);
    }

    [Fact]
    public async Task UpdateDuplicateSku_DoesNotMutateProduct()
    {
        _products.Setup(repository => repository.ExistsBySkuAsync("SKU-1", 7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ConflictException>(() => _service.UpdateProductAsync(7, ValidUpdate()));
        Assert.Equal("Laptop", _existing.Name);
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateStaleVersion_ThrowsConflict()
    {
        var dto = ValidUpdate(); dto.RowVersion = Version(2);
        await Assert.ThrowsAsync<ConflictException>(() => _service.UpdateProductAsync(7, dto));
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateMissingProduct_ThrowsNotFound()
    {
        _products.Setup(repository => repository.GetForUpdateAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Product?)null);
        await Assert.ThrowsAsync<NotFoundException>(() => _service.UpdateProductAsync(99, ValidUpdate()));
        VerifyNoWrites();
    }

    [Fact]
    public async Task SoftDelete_MarksEntityInactiveAndKeepsItsData()
    {
        await _service.DeleteProductAsync(7, Version(1));
        Assert.True(_existing.IsDeleted);
        Assert.False(_existing.IsActive);
        Assert.Equal("SKU-1", _existing.SKU);
        _products.Verify(repository => repository.SoftDelete(_existing, It.Is<byte[]>(value => value.SequenceEqual(Version(1)))), Times.Once);
        _products.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public async Task InvalidOrInsufficientStockChange_DoesNotPersist(int change)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateStockAsync(7, new() { QuantityChange = change, RowVersion = Version(1) }));
        Assert.Equal(5, _existing.Stock);
        VerifyNoWrites();
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(3, 8)]
    public async Task ValidStockChange_UpdatesAndMaps(int change, int expected)
    {
        var response = await _service.UpdateStockAsync(7, new() { QuantityChange = change, RowVersion = Version(1) });
        Assert.Equal(expected, response.Stock);
        _products.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StockOverflow_IsRejectedWithoutWrapping()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateStockAsync(7, new() { QuantityChange = int.MaxValue, RowVersion = Version(1) }));
        Assert.Equal(5, _existing.Stock);
        VerifyNoWrites();
    }

    [Fact]
    public async Task GetProducts_PreservesPaginationAndMapsPrimaryImage()
    {
        var query = new ProductQueryDto { PageNumber = 2, PageSize = 1, Search = "Laptop", SortBy = ProductSort.PriceAscending };
        _existing.Images.Add(new() { ImageUrl = "https://example.com/main.png", IsPrimary = true });
        _products.Setup(repository => repository.GetAllAsync(query, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Product>(new[] { _existing }, 3, 2, 1));
        var response = await _service.GetProductsAsync(query);
        Assert.Equal(3, response.TotalCount);
        Assert.Equal(3, response.TotalPages);
        Assert.Equal(2, response.PageNumber);
        Assert.Equal("https://example.com/main.png", Assert.Single(response.Items).PrimaryImageUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetById_PassesVisibilityAndCancellationToRepository(bool includeInactive)
    {
        using var source = new CancellationTokenSource();
        _products.Setup(repository => repository.GetByIdAsync(7, includeInactive, source.Token)).ReturnsAsync(_existing);
        var response = await _service.GetProductByIdAsync(7, includeInactive, source.Token);
        Assert.Equal(7, response.Id);
        _products.Verify(repository => repository.GetByIdAsync(7, includeInactive, source.Token), Times.Once);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task InvalidPagination_IsRejectedBeforeQuerying(int page, int size)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.GetProductsAsync(new() { PageNumber = page, PageSize = size }));
        Assert.Empty(_products.Invocations);
    }

    [Fact]
    public async Task InvalidPriceRange_IsRejectedBeforeQuerying()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.GetProductsAsync(new() { MinPrice = 100, MaxPrice = 50 }));
        Assert.Empty(_products.Invocations);
    }

    [Fact]
    public async Task PersistenceConflict_RemainsAnApplicationConflict()
    {
        _products.Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ConflictException("Concurrent duplicate."));
        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateProductAsync(ValidCreate()));
    }

    [Fact]
    public async Task NullRequest_ReturnsApplicationValidationError()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProductAsync(null!));
        VerifyNoWrites();
    }

    private void VerifyNoWrites()
    {
        _products.Verify(repository => repository.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
        _products.Verify(repository => repository.Update(It.IsAny<Product>(), It.IsAny<byte[]>()), Times.Never);
        _products.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CreateProductDto ValidCreate() => new()
    {
        Name = "New laptop", Description = "New description", SKU = "NEW-SKU", Brand = "Dell",
        Price = 100, DiscountPrice = 90, Stock = 5, CategoryId = 1
    };

    private static UpdateProductDto ValidUpdate() => new()
    {
        Name = "Updated laptop", Description = "Updated description", SKU = "SKU-1", Brand = "Dell",
        Price = 120, Stock = 8, CategoryId = 1, RowVersion = Version(1)
    };

    private static byte[] Version(byte lastByte) => new byte[] { 0, 0, 0, 0, 0, 0, 0, lastByte };
}
