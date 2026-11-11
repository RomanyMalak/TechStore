using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/orders"), Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrdersController(ICheckoutService checkout, IOrderService orders) : ControllerBase
{
    [HttpGet("by-request/{requestId:guid}")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponseDto>> ByRequest(Guid requestId, CancellationToken token) =>
        Ok(await checkout.GetOrderByRequestAsync(CartIdentity.UserId(User), requestId, token));
    [HttpPost]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<OrderResponseDto>> Create(CreateOrderDto dto, CancellationToken token)
    {
        var order = await checkout.CreateOrderAsync(CartIdentity.UserId(User), dto, token);
        return CreatedAtAction(nameof(Get), new { id = order.Id }, order);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderListDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<OrderListDto>>> GetAll([FromQuery] OrderQueryDto query, CancellationToken token) =>
        Ok(await orders.GetCustomerOrdersAsync(CartIdentity.UserId(User), query, token));

    [HttpGet("{id:int:min(1)}")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponseDto>> Get(int id, CancellationToken token) =>
        Ok(await orders.GetCustomerOrderAsync(CartIdentity.UserId(User), id, token));

    [HttpGet("{id:int:min(1)}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<OrderStatusHistoryResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrderStatusHistoryResponseDto>>> History(int id, CancellationToken token) =>
        Ok((await orders.GetCustomerOrderAsync(CartIdentity.UserId(User), id, token)).StatusHistory);

    [HttpPost("{id:int:min(1)}/cancel")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponseDto>> Cancel(int id, CancelOrderDto dto, CancellationToken token) =>
        Ok(await orders.CancelCustomerOrderAsync(CartIdentity.UserId(User), id, dto, token));
}
