using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Products;

// A full update uses the same editable fields as creation, plus the version read by the client.
public class UpdateProductDto : CreateProductDto
{
    [Required, MinLength(8), MaxLength(8)]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
