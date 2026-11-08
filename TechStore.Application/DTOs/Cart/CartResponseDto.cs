namespace TechStore.Application.DTOs.Cart;

public class CartResponseDto
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int TotalQuantity { get; set; }
    public decimal SubTotal { get; set; }
    public decimal OriginalSubTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public bool IsGuest { get; set; }
    public bool CanCheckout { get; set; }
    public List<CartItemResponseDto> Items { get; set; } = new();
}
