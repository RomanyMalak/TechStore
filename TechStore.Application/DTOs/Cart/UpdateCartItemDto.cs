using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Cart;

public class UpdateCartItemDto
{
    [Range(1, int.MaxValue)]
    public int Quantity { get; set; } = 1;
}
