using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.Common;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.AdminAccounts;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/admin/admins"), Authorize(Roles = Roles.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAccountsController(IAdminAccountService accounts) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AdminAccountResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AdminAccountResponseDto>>> GetAll([FromQuery] AdminAccountQueryDto query,
        CancellationToken token) => Ok(await accounts.GetAdminsAsync(query, token));

    [HttpPost]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(AdminAccountResponseDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<AdminAccountResponseDto>> Create(CreateAdminDto dto, CancellationToken token) =>
        StatusCode(StatusCodes.Status201Created, await accounts.CreateAdminAsync(CartIdentity.UserId(User), dto, token));
}
