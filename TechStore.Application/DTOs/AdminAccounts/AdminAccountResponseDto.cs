namespace TechStore.Application.DTOs.AdminAccounts;

public sealed class AdminAccountResponseDto
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    // Existing seed/provisioned administrators may have no recorded creator.
    public string? CreatedByAdminId { get; set; }
}
