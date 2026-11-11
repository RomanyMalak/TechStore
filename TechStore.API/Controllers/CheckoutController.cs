using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechStore.API.Http;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/checkout"), Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CheckoutController(ICheckoutService checkout) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CheckoutQuoteDto>> Get(CancellationToken token) => Ok(await checkout.GetQuoteAsync(CartIdentity.UserId(User), token));
}
