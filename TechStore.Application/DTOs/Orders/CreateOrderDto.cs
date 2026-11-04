using System.ComponentModel.DataAnnotations;
using TechStore.Domain.Enums;

namespace TechStore.Application.DTOs.Orders;

public class CreateOrderDto
{
    // The server must verify ownership, read the cart and copy the selected address.
    [Range(1, int.MaxValue)]
    public int ShippingAddressId { get; set; }

    [EnumDataType(typeof(PaymentMethod))]
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;
}
