using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.AdminAccounts;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Identity;
using TechStore.Application.Interfaces.Services;

namespace TechStore.Application.Services;

public sealed class AdminAccountService(IIdentityService identity, IValidator<CreateAdminDto> createValidator,
    IValidator<AdminAccountQueryDto> queryValidator) : IAdminAccountService
{
    public async Task<AdminAccountResponseDto> CreateAdminAsync(string actorId, CreateAdminDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450) throw new ForbiddenException();
        await RequestValidation.ValidateAsync(createValidator, dto, cancellationToken);
        // Identity checks the actor's actual stored Admin membership and performs
        // user creation, fixed-role assignment, and creator auditing atomically.
        return await identity.CreateAdminAsync(actorId, dto, cancellationToken);
    }

    public async Task<PagedResult<AdminAccountResponseDto>> GetAdminsAsync(AdminAccountQueryDto query,
        CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(queryValidator, query, cancellationToken);
        return await identity.GetAdminsAsync(query, cancellationToken);
    }
}
