using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public sealed class OrderQueryDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public OrderStatus? Status { get; set; }
    public DateTime? FromDateUtc { get; set; }
    public DateTime? ToDateUtc { get; set; }
}
