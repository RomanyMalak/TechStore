using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.Auth;

public class LoginDto
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}
