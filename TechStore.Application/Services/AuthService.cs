using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.Interfaces.Identity;
using TechStore.Application.Interfaces.Services;

namespace TechStore.Application.Services;

public sealed class AuthService(
    IIdentityService identity,
    IAccessTokenGenerator tokenGenerator,
    IValidator<RegisterDto> registerValidator,
    IValidator<LoginDto> loginValidator) : IAuthService
{
    public async Task<UserResponseDto> RegisterAsync(RegisterDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(registerValidator, dto, cancellationToken);
        return await identity.CreateCustomerAsync(dto, cancellationToken);
    }

    public async Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(loginValidator, dto, cancellationToken);
        var user = await identity.AuthenticateAsync(dto.Email.Trim(), dto.Password, cancellationToken);
        var token = tokenGenerator.Generate(user);
        return new AuthResponseDto { AccessToken = token.AccessToken, ExpiresAtUtc = token.ExpiresAtUtc, User = user };
    }
}
