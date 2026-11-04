namespace TechStore.Application.DTOs.Cart;

public class CartResponseDto
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int TotalQuantity { get; set; }
    public decimal SubTotal { get; set; }
    public List<CartItemResponseDto> Items { get; set; } = new();
}
