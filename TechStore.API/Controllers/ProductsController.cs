using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.Common;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Interfaces.Services;

namespace TechStore.API.Controllers;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(IProductService products) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResult<ProductListDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ProductListDto>>> GetAll([FromQuery] ProductQueryDto query, CancellationToken cancellationToken) =>
        Ok(await products.GetProductsAsync(query, User.IsInRole(Roles.Admin), cancellationToken));

    [HttpGet("summary")]
    [Authorize(Roles = Roles.Admin)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProductSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductSummaryDto>> GetSummary(CancellationToken cancellationToken) =>
        Ok(await products.GetSummaryAsync(cancellationToken));

    [HttpGet("{id:int:min(1)}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponseDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var product = await products.GetProductByIdAsync(id, User.IsInRole(Roles.Admin), cancellationToken);
        RowVersionHeader.SetETag(Response, product.RowVersion);
        return Ok(product);
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ProductResponseDto>> Create(CreateProductDto dto, CancellationToken cancellationToken)
    {
        var product = await products.CreateProductAsync(dto, cancellationToken);
        RowVersionHeader.SetETag(Response, product.RowVersion);
        return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
    }

    [HttpPut("{id:int:min(1)}")]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductResponseDto>> Update(int id, UpdateProductDto dto, CancellationToken cancellationToken)
    {
        var product = await products.UpdateProductAsync(id, dto, cancellationToken);
        RowVersionHeader.SetETag(Response, product.RowVersion);
        return Ok(product);
    }

    [HttpDelete("{id:int:min(1)}")]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken cancellationToken)
    {
        await products.DeleteProductAsync(id, RowVersionHeader.ParseRequired(ifMatch), cancellationToken);
        return NoContent();
    }

    [HttpPatch("{id:int:min(1)}/stock")]
    [Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductResponseDto>> UpdateStock(int id, UpdateStockDto dto, CancellationToken cancellationToken)
    {
        var product = await products.UpdateStockAsync(id, dto, cancellationToken);
        RowVersionHeader.SetETag(Response, product.RowVersion);
        return Ok(product);
    }
}
