namespace TechStore.Application.DTOs.Auth;

public sealed record AccessTokenResult(string AccessToken, DateTime ExpiresAtUtc);
