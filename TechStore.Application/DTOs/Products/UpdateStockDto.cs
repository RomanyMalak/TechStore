namespace TechStore.Application.DTOs.Products;

public class UpdateStockDto
{
    // Positive values receive stock; negative values remove stock. This is not an absolute total.
    public int QuantityChange { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
