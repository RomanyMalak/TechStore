using TechStore.Application.DTOs.Addresses;
using TechStore.Domain.Entities;

namespace TechStore.Application.Mapping;

internal static class AddressMappings
{
    public static AddressResponseDto ToAddressResponse(this Address address) => new()
    {
        Id = address.Id, FullName = address.FullName, Phone = address.Phone, City = address.City,
        Street = address.Street, Building = address.Building, IsDefault = address.IsDefault
    };
}
