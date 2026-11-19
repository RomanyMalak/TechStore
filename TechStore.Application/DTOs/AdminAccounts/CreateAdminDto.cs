using System.ComponentModel.DataAnnotations;

namespace TechStore.Application.DTOs.AdminAccounts;

public sealed class CreateAdminDto
{
    [Required, StringLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}
