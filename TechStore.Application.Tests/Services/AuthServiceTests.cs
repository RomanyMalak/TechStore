using Moq;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Identity;
using TechStore.Application.Services;
using TechStore.Application.Validators.Auth;

namespace TechStore.Application.Tests.Services;

public class AuthServiceTests
{
    private readonly Mock<IIdentityService> _identity = new(MockBehavior.Strict);
    private readonly Mock<IAccessTokenGenerator> _tokens = new(MockBehavior.Strict);
    private readonly AuthService _service;

    public AuthServiceTests() => _service = new AuthService(_identity.Object, _tokens.Object,
        new RegisterDtoValidator(), new LoginDtoValidator());

    [Fact]
    public async Task Register_ValidatesAndDelegatesCustomerCreation()
    {
        var request = ValidRegistration();
        var user = new UserResponseDto { Id = "customer-id", Roles = new() { "Customer" } };
        _identity.Setup(identity => identity.CreateCustomerAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        Assert.Same(user, await _service.RegisterAsync(request));
        _tokens.Verify(generator => generator.Generate(It.IsAny<UserResponseDto>()), Times.Never);
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Password")]
    [InlineData("ConfirmPassword")]
    [InlineData("FullName")]
    public async Task Register_InvalidInput_DoesNotReachIdentity(string field)
    {
        var request = ValidRegistration();
        switch (field)
        {
            case "Email": request.Email = "invalid"; break;
            case "Password": request.Password = "short"; break;
            case "ConfirmPassword": request.ConfirmPassword = "different"; break;
            case "FullName": request.FullName = " "; break;
        }
        var error = await Assert.ThrowsAsync<ValidationException>(() => _service.RegisterAsync(request));
        Assert.Contains(field, error.Errors.Keys);
        Assert.DoesNotContain(request.Password, string.Join(" ", error.Errors.SelectMany(pair => pair.Value)));
        Assert.Empty(_identity.Invocations);
    }

    [Fact]
    public async Task Login_UsesIdentityRolesAndMapsTokenMetadata()
    {
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        var user = new UserResponseDto { Id = "user-id", Roles = new() { "Customer" } };
        var expiry = DateTime.UtcNow.AddMinutes(60);
        _identity.Setup(identity => identity.AuthenticateAsync("customer@example.com", password, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokens.Setup(generator => generator.Generate(user)).Returns(new AccessTokenResult("opaque-test-result", expiry));
        var response = await _service.LoginAsync(new() { Email = "customer@example.com", Password = password });
        Assert.Equal("Bearer", response.TokenType);
        Assert.Equal(expiry, response.ExpiresAtUtc);
        Assert.Equal("Customer", Assert.Single(response.User.Roles));
    }

    [Fact]
    public async Task Login_InvalidCredentials_DoesNotGenerateToken()
    {
        var password = Guid.NewGuid().ToString("N");
        _identity.Setup(identity => identity.AuthenticateAsync("customer@example.com", password, It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedException());
        await Assert.ThrowsAsync<UnauthorizedException>(() => _service.LoginAsync(new() { Email = "customer@example.com", Password = password }));
        Assert.Empty(_tokens.Invocations);
    }

    [Fact]
    public async Task Login_InvalidDto_DoesNotReachIdentity()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.LoginAsync(new() { Email = "invalid", Password = "" }));
        Assert.Empty(_identity.Invocations);
    }

    private static RegisterDto ValidRegistration()
    {
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        return new RegisterDto { FullName = "Customer", Email = "customer@example.com", Password = password, ConfirmPassword = password };
    }
}
