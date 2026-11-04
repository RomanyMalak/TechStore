using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public class OrderStatusHistoryResponseDto
{
    public int Id { get; set; }
    public OrderStatus? OldStatus { get; set; }
    public OrderStatus NewStatus { get; set; }
    public string? ChangedByUserId { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? Note { get; set; }
}
