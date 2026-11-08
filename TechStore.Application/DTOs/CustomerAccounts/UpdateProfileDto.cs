using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.CustomerAccounts;

public sealed class UpdateProfileDto
{
    [Required, StringLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Phone, StringLength(30)]
    public string? PhoneNumber { get; set; }
}
