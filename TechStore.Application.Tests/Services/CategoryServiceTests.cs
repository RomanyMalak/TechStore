using Moq;
using TechStore.Application.DTOs.Categories;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Services;
using TechStore.Application.Validators.Categories;
using TechStore.Domain.Entities;

namespace TechStore.Application.Tests.Services;

public class CategoryServiceTests
{
    private readonly Mock<ICategoryRepository> _categories = new(MockBehavior.Strict);
    private readonly Category _existing = new() { Id = 1, Name = "Computers" };
    private readonly List<Category> _tree = new();
    private readonly CategoryService _service;

    public CategoryServiceTests()
    {
        _tree.Add(_existing);
        _categories.Setup(repository => repository.GetAllAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<Category>)_tree.ToArray());
        _categories.Setup(repository => repository.ExistsByNameAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _categories.Setup(repository => repository.GetForUpdateAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_existing);
        _categories.Setup(repository => repository.AddAsync(It.IsAny<Category>(), It.IsAny<CancellationToken>()))
            .Callback<Category, CancellationToken>((category, _) => category.Id = 10).Returns(Task.CompletedTask);
        _categories.Setup(repository => repository.Update(It.IsAny<Category>()));
        _categories.Setup(repository => repository.Deactivate(It.IsAny<Category>()));
        _categories.Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _categories.Setup(repository => repository.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task<CategoryResponseDto>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<CategoryResponseDto>> action, CancellationToken token) => action(token));
        _categories.Setup(repository => repository.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<bool>> action, CancellationToken token) => action(token));
        _service = new CategoryService(_categories.Object, new CreateCategoryDtoValidator(), new UpdateCategoryDtoValidator());
    }

    [Fact]
    public async Task CreateValidSubcategory_MapsItsParentAndSaves()
    {
        var response = await _service.CreateCategoryAsync(new() { Name = "  Laptops  ", ParentCategoryId = 1 });
        Assert.Equal(10, response.Id);
        Assert.Equal("Laptops", response.Name);
        Assert.Equal("Computers", response.ParentCategoryName);
        _categories.Verify(repository => repository.AddAsync(It.Is<Category>(category => category.ParentCategoryId == 1), It.IsAny<CancellationToken>()), Times.Once);
        _categories.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRootCategory_DoesNotRequireAParent()
    {
        var response = await _service.CreateCategoryAsync(new() { Name = "Phones" });
        Assert.Null(response.ParentCategoryId);
        Assert.Null(response.ParentCategoryName);
    }

    [Fact]
    public async Task CreateMissingParent_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.CreateCategoryAsync(new() { Name = "Laptops", ParentCategoryId = 99 }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task CreateActiveCategoryUnderInactiveParent_IsRejected()
    {
        _existing.Deactivate();
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateCategoryAsync(new() { Name = "Laptops", ParentCategoryId = 1 }));
        VerifyNoWrites();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyName_IsRejectedBeforeDataAccess(string name)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateCategoryAsync(new() { Name = name }));
        Assert.Empty(_categories.Invocations);
    }

    [Fact]
    public async Task LongDescription_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateCategoryAsync(new() { Name = "Phones", Description = new string('D', 501) }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task DuplicateName_ThrowsConflict()
    {
        _categories.Setup(repository => repository.ExistsByNameAsync("Phones", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateCategoryAsync(new() { Name = " Phones " }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateSelfParent_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateCategoryAsync(1, new() { Name = "Computers", ParentCategoryId = 1 }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateParentToDirectChild_IsRejected()
    {
        _tree.Add(new() { Id = 2, Name = "Laptops", ParentCategoryId = 1 });
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateCategoryAsync(1, new() { Name = "Computers", ParentCategoryId = 2 }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateParentToDeepDescendant_IsRejected()
    {
        _tree.Add(new() { Id = 2, Name = "Laptops", ParentCategoryId = 1 });
        _tree.Add(new() { Id = 3, Name = "Gaming laptops", ParentCategoryId = 2 });
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateCategoryAsync(1, new() { Name = "Computers", ParentCategoryId = 3 }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task AlreadyCircularParentChain_IsRejectedWithoutInfiniteTraversal()
    {
        _tree.Add(new() { Id = 2, Name = "Broken A", ParentCategoryId = 3 });
        _tree.Add(new() { Id = 3, Name = "Broken B", ParentCategoryId = 2 });
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateCategoryAsync(new() { Name = "Child", ParentCategoryId = 2 }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task ValidReparenting_ExcludesOwnNameAndMapsNewParent()
    {
        _tree.Add(new() { Id = 2, Name = "Electronics" });
        var response = await _service.UpdateCategoryAsync(1, new() { Name = "Computers", ParentCategoryId = 2 });
        Assert.Equal(2, _existing.ParentCategoryId);
        Assert.Equal("Electronics", response.ParentCategoryName);
        _categories.Verify(repository => repository.ExistsByNameAsync("Computers", 1, It.IsAny<CancellationToken>()), Times.Once);
        _categories.Verify(repository => repository.Update(_existing), Times.Once);
    }

    [Fact]
    public async Task RemovingParent_ClearsParentInResponse()
    {
        _existing.ParentCategoryId = 2;
        _existing.ParentCategory = new() { Id = 2, Name = "Electronics" };
        _tree.Add(_existing.ParentCategory);
        var response = await _service.UpdateCategoryAsync(1, new() { Name = "Computers" });
        Assert.Null(response.ParentCategoryId);
        Assert.Null(response.ParentCategoryName);
    }

    [Fact]
    public async Task MissingUpdateTarget_ThrowsNotFound()
    {
        _categories.Setup(repository => repository.GetForUpdateAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);
        await Assert.ThrowsAsync<NotFoundException>(() => _service.UpdateCategoryAsync(99, new() { Name = "Missing" }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task DeactivateCategory_PreservesItsRecord()
    {
        await _service.DeactivateCategoryAsync(1);
        Assert.False(_existing.IsActive);
        Assert.Equal("Computers", _existing.Name);
        _categories.Verify(repository => repository.Deactivate(_existing), Times.Once);
        _categories.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeactivateWithActiveChildren_IsRejected()
    {
        _tree.Add(new() { Id = 2, Name = "Laptops", ParentCategoryId = 1 });
        await Assert.ThrowsAsync<ConflictException>(() => _service.DeactivateCategoryAsync(1));
        Assert.True(_existing.IsActive);
        VerifyNoWrites();
    }

    [Fact]
    public async Task UpdateCannotBypassDeactivationRule()
    {
        _tree.Add(new() { Id = 2, Name = "Laptops", ParentCategoryId = 1 });
        await Assert.ThrowsAsync<ConflictException>(() => _service.UpdateCategoryAsync(1, new() { Name = "Computers", IsActive = false }));
        VerifyNoWrites();
    }

    [Fact]
    public async Task ReadCategories_UsesRequestedVisibilityAndMapsDtos()
    {
        var responses = await _service.GetCategoriesAsync(includeInactive: true);
        Assert.Equal("Computers", Assert.Single(responses).Name);
        _categories.Verify(repository => repository.GetAllAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyNoWrites()
    {
        _categories.Verify(repository => repository.AddAsync(It.IsAny<Category>(), It.IsAny<CancellationToken>()), Times.Never);
        _categories.Verify(repository => repository.Update(It.IsAny<Category>()), Times.Never);
        _categories.Verify(repository => repository.Deactivate(It.IsAny<Category>()), Times.Never);
        _categories.Verify(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
