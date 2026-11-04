using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Cart;

public class AddToCartDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; } = 1;
}
