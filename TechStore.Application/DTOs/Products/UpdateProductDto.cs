using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace TechStore.Application.DTOs.Products;

// A full update uses the same editable fields as creation, plus the version read by the client.
public class UpdateProductDto : CreateProductDto
{
    // An omitted property keeps existing images; an explicit [] deliberately removes them.
    private bool imagesSpecified;
    [JsonIgnore]
    public bool ImagesSpecified => imagesSpecified || base.Images is { Count: > 0 };
    public override List<ProductImageRequestDto> Images
    {
        get => base.Images;
        set { base.Images = value; imagesSpecified = true; }
    }

    [Required, MinLength(8), MaxLength(8)]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
