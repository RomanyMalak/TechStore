using TechStore.Application.DTOs.Auth;

namespace TechStore.Application.Interfaces.Identity;

public interface IAccessTokenGenerator
{
    AccessTokenResult Generate(UserResponseDto user);
}
