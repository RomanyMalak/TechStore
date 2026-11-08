using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public sealed class OrderListDto
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public OrderStatus Status { get; set; }
    public decimal TotalAmount { get; set; }
    public long TotalQuantity { get; set; }
}
