using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TechStore.API.Configuration;
using TechStore.API.Http;
using TechStore.Application.Common;
using TechStore.Application.Common.Security;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Interfaces.Storage;

namespace TechStore.API.Controllers;

[ApiController, Route("api/product-images")]
public sealed class ProductImagesController(IProductImageStore images) : ControllerBase
{
    public sealed class UploadForm
    {
        [Required] public IFormFile File { get; set; } = null!;
    }

    [HttpPost, Authorize(Roles = Roles.Admin)]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [RequestSizeLimit(ProductImagePolicy.MaximumUploadBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ProductImagePolicy.MaximumUploadBytes + 64 * 1024)]
    [ProducesResponseType(typeof(ProductImageUploadDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<ProductImageUploadDto>> Upload([FromForm] UploadForm form, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (form.File.Length > ProductImagePolicy.MaximumUploadBytes)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The image exceeds the five MiB limit.");
        if (form.File.Length == 0 || Request.Form.Files.Count != 1)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Choose one non-empty image per upload request." });
        await using var content = form.File.OpenReadStream();
        var uploaded = await images.UploadAsync(content, form.File.FileName, form.File.ContentType, CartIdentity.UserId(User), token);
        return Created(uploaded.ImageUrl, uploaded);
    }

    [HttpGet("{fileName}"), AllowAnonymous]
    [Produces("image/webp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string fileName, CancellationToken token)
    {
        if (!ProductImagePolicy.TryGetManagedId(ProductImagePolicy.PublicPath + fileName, out _)) return NotFound();
        var image = await images.OpenReadAsync(fileName, token);
        if (image is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(image.Content, image.ContentType, enableRangeProcessing: false);
    }

    [HttpDelete("{id:guid}"), Authorize(Roles = Roles.Admin)]
    [EnableRateLimiting(ApiRateLimiting.SensitiveWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteDraft(Guid id, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        await images.DeleteDraftAsync(id, CartIdentity.UserId(User), token);
        return NoContent();
    }
}
