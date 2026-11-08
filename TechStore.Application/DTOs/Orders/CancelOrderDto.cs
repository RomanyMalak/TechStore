using System.ComponentModel.DataAnnotations;
using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public sealed class CancelOrderDto
{
    [Required, EnumDataType(typeof(OrderStatus))]
    public OrderStatus? ExpectedStatus { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }
}
