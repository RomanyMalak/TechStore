using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/cart")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting(ApiRateLimiting.Cart)]
public sealed class CartController(ICartService cart) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<ActionResult<CartResponseDto>> Get(CancellationToken token) => Ok(await cart.GetAsync(CartIdentity.Owner(HttpContext), token));
    [HttpPost("items"), AllowAnonymous]
    public async Task<ActionResult<CartResponseDto>> Add(AddToCartDto dto, CancellationToken token) => Ok(await cart.AddAsync(CartIdentity.Owner(HttpContext), dto, token));
    [HttpPut("items/{productId:int:min(1)}"), AllowAnonymous]
    public async Task<ActionResult<CartResponseDto>> Update(int productId, UpdateCartItemDto dto, CancellationToken token) => Ok(await cart.UpdateAsync(CartIdentity.Owner(HttpContext), productId, dto, token));
    [HttpDelete("items/{productId:int:min(1)}"), AllowAnonymous]
    public async Task<ActionResult<CartResponseDto>> Remove(int productId, CancellationToken token) => Ok(await cart.RemoveAsync(CartIdentity.Owner(HttpContext), productId, token));
    [HttpPost("merge"), Authorize]
    public async Task<ActionResult<CartResponseDto>> Merge(CancellationToken token)
    {
        var result = await cart.MergeAsync(CartIdentity.UserId(User), CartIdentity.GuestKey(HttpContext), token);
        CartIdentity.ClearGuest(HttpContext);
        return Ok(result);
    }
}
