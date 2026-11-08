using System.ComponentModel.DataAnnotations;
using TechStore.Domain.Enums;
using TechStore.Application.DTOs.Addresses;

namespace TechStore.Application.DTOs.Orders;

public class CreateOrderDto
{
    // The server must verify ownership, read the cart and copy the selected address.
    public int? ShippingAddressId { get; set; }
    public CreateAddressDto? ShippingAddress { get; set; }
    public Guid ClientRequestId { get; set; }
    public string QuoteVersion { get; set; } = string.Empty;

    [EnumDataType(typeof(PaymentMethod))]
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;
}
