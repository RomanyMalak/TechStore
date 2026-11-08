using TechStore.Application.Common;
using TechStore.Application.DTOs.AdminAccounts;

namespace TechStore.Application.Interfaces.Services;

public interface IAdminAccountService
{
    Task<AdminAccountResponseDto> CreateAdminAsync(string actorId, CreateAdminDto dto, CancellationToken cancellationToken = default);
    Task<PagedResult<AdminAccountResponseDto>> GetAdminsAsync(AdminAccountQueryDto query, CancellationToken cancellationToken = default);
}
