namespace TechStore.Application.DTOs.AdminAccounts;

public sealed class AdminAccountQueryDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
}
