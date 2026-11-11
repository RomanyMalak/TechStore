using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Analytics;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController, Route("api/admin/analytics"), Authorize(Roles = Roles.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAnalyticsController(IAnalyticsService analytics) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AnalyticsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AnalyticsResponseDto>> Get([FromQuery] AnalyticsQueryDto query,
        CancellationToken cancellationToken) => Ok(await analytics.GetAsync(query, cancellationToken));
}
