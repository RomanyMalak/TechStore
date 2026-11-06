using Microsoft.AspNetCore.Identity;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Identity;
using TechStore.Infrastructure.Persistence;

namespace TechStore.Infrastructure.Identity;

public sealed class IdentityService(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    ApplicationDbContext context,
    IUserConfirmation<ApplicationUser> confirmation) : IIdentityService
{
    public async Task<UserResponseDto> CreateCustomerAsync(RegisterDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var email = dto.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            throw new ConflictException("An account with that email already exists.");
        if (!await roles.RoleExistsAsync(Roles.Customer))
            throw new InvalidOperationException("Identity roles must be initialized before accepting registrations.");

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var user = new ApplicationUser
        {
            UserName = email, Email = email, FullName = dto.FullName.Trim(), PhoneNumber = dto.PhoneNumber?.Trim()
        };
        EnsureRegistrationSucceeded(await users.CreateAsync(user, dto.Password));
        // Role assignment and user creation either both succeed or both roll back.
        EnsureRegistrationSucceeded(await users.AddToRoleAsync(user, Roles.Customer));
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return await ToResponseAsync(user);
    }

    public async Task<UserResponseDto> AuthenticateAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !await CanUsePasswordLoginAsync(user)) throw new UnauthorizedException();
        if (!await users.CheckPasswordAsync(user, password))
        {
            if (users.SupportsUserLockout) await users.AccessFailedAsync(user);
            throw new UnauthorizedException();
        }
        if (users.SupportsUserLockout && !(await users.ResetAccessFailedCountAsync(user)).Succeeded)
            throw new UnauthorizedException();
        cancellationToken.ThrowIfCancellationRequested();
        return await ToResponseAsync(user);
    }

    private async Task<bool> CanUsePasswordLoginAsync(ApplicationUser user)
    {
        if (users.SupportsUserLockout && await users.IsLockedOutAsync(user)) return false;
        if (users.Options.SignIn.RequireConfirmedEmail && !await users.IsEmailConfirmedAsync(user)) return false;
        if (users.Options.SignIn.RequireConfirmedPhoneNumber && !await users.IsPhoneNumberConfirmedAsync(user)) return false;
        if (users.Options.SignIn.RequireConfirmedAccount && !await confirmation.IsConfirmedAsync(users, user)) return false;
        // There is no second-factor completion endpoint yet. Never downgrade such an account to password-only JWTs.
        if (users.SupportsUserTwoFactor && await users.GetTwoFactorEnabledAsync(user)) return false;
        return true;
    }

    private async Task<UserResponseDto> ToResponseAsync(ApplicationUser user) => new()
    {
        Id = user.Id, FullName = user.FullName, Email = user.Email, PhoneNumber = user.PhoneNumber,
        CreatedAt = user.CreatedAt, Roles = (await users.GetRolesAsync(user)).ToList()
    };

    private static void EnsureRegistrationSucceeded(IdentityResult result)
    {
        if (result.Succeeded) return;
        if (result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
            throw new ConflictException("An account with that email already exists.");
        throw new ValidationException(result.Errors.GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal)
                ? nameof(RegisterDto.Password) : "Registration")
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
    }
}
