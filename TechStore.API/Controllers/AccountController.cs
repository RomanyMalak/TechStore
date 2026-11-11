using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.DTOs.Addresses;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.CustomerAccounts;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/account"), Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(ICustomerAccountService account) : ControllerBase
{
    [HttpGet("profile")]
    [ProducesResponseType(typeof(UserResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserResponseDto>> GetProfile(CancellationToken token) =>
        Ok(await account.GetProfileAsync(CartIdentity.UserId(User), token));

    [HttpPut("profile")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(UserResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserResponseDto>> UpdateProfile(UpdateProfileDto dto, CancellationToken token) =>
        Ok(await account.UpdateProfileAsync(CartIdentity.UserId(User), dto, token));

    [HttpGet("addresses")]
    [ProducesResponseType(typeof(IReadOnlyList<AddressResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AddressResponseDto>>> GetAddresses(CancellationToken token) =>
        Ok(await account.GetAddressesAsync(CartIdentity.UserId(User), token));

    [HttpGet("addresses/{id:int:min(1)}")]
    [ProducesResponseType(typeof(AddressResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AddressResponseDto>> GetAddress(int id, CancellationToken token) =>
        Ok(await account.GetAddressAsync(CartIdentity.UserId(User), id, token));

    [HttpPost("addresses")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(AddressResponseDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<AddressResponseDto>> CreateAddress(CreateAddressDto dto, CancellationToken token)
    {
        var address = await account.CreateAddressAsync(CartIdentity.UserId(User), dto, token);
        return CreatedAtAction(nameof(GetAddress), new { id = address.Id }, address);
    }

    [HttpPut("addresses/{id:int:min(1)}")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(AddressResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AddressResponseDto>> UpdateAddress(int id, UpdateAddressDto dto, CancellationToken token) =>
        Ok(await account.UpdateAddressAsync(CartIdentity.UserId(User), id, dto, token));

    [HttpDelete("addresses/{id:int:min(1)}")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteAddress(int id, CancellationToken token)
    {
        await account.DeleteAddressAsync(CartIdentity.UserId(User), id, token);
        return NoContent();
    }

    [HttpPut("addresses/{id:int:min(1)}/default")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(AddressResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AddressResponseDto>> SetDefaultAddress(int id, CancellationToken token) =>
        Ok(await account.SetDefaultAddressAsync(CartIdentity.UserId(User), id, token));
}
