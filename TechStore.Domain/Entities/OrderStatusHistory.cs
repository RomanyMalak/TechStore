using TechStore.Domain.Enums;

namespace TechStore.Domain.Entities;

public class OrderStatusHistory
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    // The initial Pending entry has no previous status; system changes have no actor.
    public OrderStatus? OldStatus { get; set; }
    public OrderStatus NewStatus { get; set; } = OrderStatus.Pending;
    public string? ChangedByUserId { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}
