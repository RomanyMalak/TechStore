using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.Common;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/admin/orders"), Authorize(Roles = Roles.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminOrdersController(IOrderService orders) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderListDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<OrderListDto>>> GetAll([FromQuery] OrderQueryDto query, CancellationToken token) =>
        Ok(await orders.GetAdminOrdersAsync(query, token));

    [HttpGet("{id:int:min(1)}")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponseDto>> Get(int id, CancellationToken token) =>
        Ok(await orders.GetAdminOrderAsync(id, token));

    [HttpGet("{id:int:min(1)}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<OrderStatusHistoryResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrderStatusHistoryResponseDto>>> History(int id, CancellationToken token) =>
        Ok((await orders.GetAdminOrderAsync(id, token)).StatusHistory);

    [HttpPatch("{id:int:min(1)}/status")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponseDto>> UpdateStatus(int id, UpdateOrderStatusDto dto, CancellationToken token) =>
        Ok(await orders.UpdateStatusAsync(CartIdentity.UserId(User), id, dto, token));
}
