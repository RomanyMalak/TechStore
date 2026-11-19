using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Cart;

public class AddToCartDto
{
    // Optional for older clients; clients reuse this value only when retrying the same add.
    public Guid? ClientRequestId { get; set; }

    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; } = 1;
}
