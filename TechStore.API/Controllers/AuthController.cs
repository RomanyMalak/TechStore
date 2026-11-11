using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting(ApiRateLimiting.Registration)]
    [ProducesResponseType(typeof(UserResponseDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<UserResponseDto>> Register(RegisterDto dto, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, await auth.RegisterAsync(dto, cancellationToken));
    }

    [HttpPost("login")]
    [EnableRateLimiting(ApiRateLimiting.Login)]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponseDto>> Login(LoginDto dto, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(await auth.LoginAsync(dto, cancellationToken));
    }
}
