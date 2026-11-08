using TechStore.Application.DTOs.Addresses;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.CustomerAccounts;

namespace TechStore.Application.Interfaces.Services;

public interface ICustomerAccountService
{
    Task<UserResponseDto> GetProfileAsync(string userId, CancellationToken cancellationToken = default);
    Task<UserResponseDto> UpdateProfileAsync(string userId, UpdateProfileDto dto, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AddressResponseDto>> GetAddressesAsync(string userId, CancellationToken cancellationToken = default);
    Task<AddressResponseDto> GetAddressAsync(string userId, int id, CancellationToken cancellationToken = default);
    Task<AddressResponseDto> CreateAddressAsync(string userId, CreateAddressDto dto, CancellationToken cancellationToken = default);
    Task<AddressResponseDto> UpdateAddressAsync(string userId, int id, UpdateAddressDto dto, CancellationToken cancellationToken = default);
    Task DeleteAddressAsync(string userId, int id, CancellationToken cancellationToken = default);
    Task<AddressResponseDto> SetDefaultAddressAsync(string userId, int id, CancellationToken cancellationToken = default);
}
