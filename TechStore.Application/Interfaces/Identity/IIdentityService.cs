using TechStore.Application.DTOs.Auth;
using TechStore.Application.Common;
using TechStore.Application.DTOs.AdminAccounts;
using TechStore.Application.DTOs.CustomerAccounts;

namespace TechStore.Application.Interfaces.Identity;

public interface IIdentityService
{
    Task<UserResponseDto> CreateCustomerAsync(RegisterDto dto, CancellationToken cancellationToken = default);
    Task<UserResponseDto> AuthenticateAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<AdminAccountResponseDto> CreateAdminAsync(string actorId, CreateAdminDto dto, CancellationToken cancellationToken = default);
    Task<PagedResult<AdminAccountResponseDto>> GetAdminsAsync(AdminAccountQueryDto query, CancellationToken cancellationToken = default);
    Task<UserResponseDto> GetProfileAsync(string userId, CancellationToken cancellationToken = default);
    Task<UserResponseDto> UpdateProfileAsync(string userId, UpdateProfileDto dto, CancellationToken cancellationToken = default);
}
