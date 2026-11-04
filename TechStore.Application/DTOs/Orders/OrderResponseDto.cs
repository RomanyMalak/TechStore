using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public class OrderResponseDto
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public OrderStatus Status { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public decimal SubTotal { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string RecipientName { get; set; } = string.Empty;
    public string ShippingPhone { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public List<OrderItemResponseDto> Items { get; set; } = new();
    public List<OrderStatusHistoryResponseDto> StatusHistory { get; set; } = new();
}
