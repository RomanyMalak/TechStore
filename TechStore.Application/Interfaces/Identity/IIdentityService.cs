using TechStore.Application.DTOs.Auth;

namespace TechStore.Application.Interfaces.Identity;

public interface IIdentityService
{
    Task<UserResponseDto> CreateCustomerAsync(RegisterDto dto, CancellationToken cancellationToken = default);
    Task<UserResponseDto> AuthenticateAsync(string email, string password, CancellationToken cancellationToken = default);
}
