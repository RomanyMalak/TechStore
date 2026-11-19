namespace TechStore.Application.DTOs.Products;

public sealed record ProductImageUploadDto(Guid Id, string ImageUrl, long Size, int Width, int Height,
    string ContentType = "image/webp");

public sealed record ProductImageRead(Stream Content, long Length, string ContentType = "image/webp");
