using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Addresses;

public class CreateAddressDto
{
    [Required, StringLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required, Phone, StringLength(30)]
    public string Phone { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string City { get; set; } = string.Empty;

    [Required, StringLength(300)]
    public string Street { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string Building { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}
