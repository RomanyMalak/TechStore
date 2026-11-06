using TechStore.Application.DTOs.Auth;

namespace TechStore.Application.Interfaces.Services;

public interface IAuthService
{
    Task<UserResponseDto> RegisterAsync(RegisterDto dto, CancellationToken cancellationToken = default);
    Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken cancellationToken = default);
}
