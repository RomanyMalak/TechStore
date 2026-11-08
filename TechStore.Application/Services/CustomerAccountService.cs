using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Addresses;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.CustomerAccounts;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Identity;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Mapping;
using TechStore.Domain.Entities;

namespace TechStore.Application.Services;

public sealed class CustomerAccountService(IIdentityService identity, IAddressRepository addresses,
    IValidator<UpdateProfileDto> profileValidator, IValidator<CreateAddressDto> createAddressValidator,
    IValidator<UpdateAddressDto> updateAddressValidator) : ICustomerAccountService
{
    public Task<UserResponseDto> GetProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        return identity.GetProfileAsync(userId, cancellationToken);
    }

    public async Task<UserResponseDto> UpdateProfileAsync(string userId, UpdateProfileDto dto, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        await RequestValidation.ValidateAsync(profileValidator, dto, cancellationToken);
        return await identity.UpdateProfileAsync(userId, dto, cancellationToken);
    }

    public async Task<IReadOnlyList<AddressResponseDto>> GetAddressesAsync(string userId, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        return (await addresses.GetAllAsync(userId, tracked: false, cancellationToken)).Select(address => address.ToAddressResponse()).ToArray();
    }

    public async Task<AddressResponseDto> GetAddressAsync(string userId, int id, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        return (await addresses.GetByIdAsync(userId, id, cancellationToken) ?? throw new NotFoundException(nameof(Address), id)).ToAddressResponse();
    }

    public async Task<AddressResponseDto> CreateAddressAsync(string userId, CreateAddressDto dto, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        await RequestValidation.ValidateAsync(createAddressValidator, dto, cancellationToken);
        return await addresses.ExecuteInTransactionAsync(async token =>
        {
            var existing = await addresses.GetAllAsync(userId, tracked: true, token);
            var makeDefault = dto.IsDefault || existing.Count == 0 || !existing.Any(address => address.IsDefault);
            if (makeDefault) await ClearOtherDefaultsAsync(existing, exceptId: null, token);
            var address = new Address { UserId = userId, IsDefault = makeDefault };
            ApplyDetails(address, dto);
            await addresses.AddAsync(address, token);
            await addresses.SaveChangesAsync(token);
            return address.ToAddressResponse();
        }, cancellationToken);
    }

    public async Task<AddressResponseDto> UpdateAddressAsync(string userId, int id, UpdateAddressDto dto,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        await RequestValidation.ValidateAsync(updateAddressValidator, dto, cancellationToken);
        return await addresses.ExecuteInTransactionAsync(async token =>
        {
            var existing = await addresses.GetAllAsync(userId, tracked: true, token);
            var address = existing.SingleOrDefault(item => item.Id == id) ?? throw new NotFoundException(nameof(Address), id);
            if (address.IsDefault && !dto.IsDefault)
                throw new ConflictException("Choose a different default address before clearing the current default.");
            var makeDefault = dto.IsDefault || !existing.Any(item => item.IsDefault);
            if (makeDefault) await ClearOtherDefaultsAsync(existing, address.Id, token);
            ApplyDetails(address, dto);
            address.IsDefault = makeDefault;
            await addresses.SaveChangesAsync(token);
            return address.ToAddressResponse();
        }, cancellationToken);
    }

    public async Task DeleteAddressAsync(string userId, int id, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        await addresses.ExecuteInTransactionAsync(async token =>
        {
            var existing = await addresses.GetAllAsync(userId, tracked: true, token);
            var address = existing.SingleOrDefault(item => item.Id == id) ?? throw new NotFoundException(nameof(Address), id);
            addresses.Delete(address);
            // Remove the existing filtered-index entry before promoting a replacement.
            await addresses.SaveChangesAsync(token);
            var remaining = existing.Where(item => item.Id != id).OrderBy(item => item.Id).ToArray();
            if (remaining.Length > 0 && (address.IsDefault || !remaining.Any(item => item.IsDefault)))
            {
                remaining[0].IsDefault = true;
                await addresses.SaveChangesAsync(token);
            }
            return true;
        }, cancellationToken);
    }

    public async Task<AddressResponseDto> SetDefaultAddressAsync(string userId, int id, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        return await addresses.ExecuteInTransactionAsync(async token =>
        {
            var existing = await addresses.GetAllAsync(userId, tracked: true, token);
            var address = existing.SingleOrDefault(item => item.Id == id) ?? throw new NotFoundException(nameof(Address), id);
            if (address.IsDefault) return address.ToAddressResponse();
            await ClearOtherDefaultsAsync(existing, address.Id, token);
            address.IsDefault = true;
            await addresses.SaveChangesAsync(token);
            return address.ToAddressResponse();
        }, cancellationToken);
    }

    private async Task ClearOtherDefaultsAsync(IReadOnlyList<Address> existing, int? exceptId, CancellationToken cancellationToken)
    {
        var defaults = existing.Where(address => address.IsDefault && address.Id != exceptId).ToArray();
        if (defaults.Length == 0) return;
        foreach (var address in defaults) address.IsDefault = false;
        // Two saves in the same transaction avoid EF command ordering producing
        // a temporary duplicate in IX_Addresses_OneDefaultPerUser.
        await addresses.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyDetails(Address address, CreateAddressDto dto)
    {
        address.FullName = dto.FullName.Trim(); address.Phone = dto.Phone.Trim(); address.City = dto.City.Trim();
        address.Street = dto.Street.Trim(); address.Building = dto.Building.Trim();
    }

    private static void RequireIdentity(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450) throw new UnauthorizedException();
    }
}
