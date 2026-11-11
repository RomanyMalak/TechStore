using System.Security.Claims;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using TechStore.Application.Common;
using TechStore.Application.Exceptions;
using TechStore.Application.Mapping;

namespace TechStore.API.Http;

internal static class CartIdentity
{
    private const string CookieName = "__Host-TechStore.Cart";
    private static readonly CookieOptions Options = new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(30), IsEssential = true };
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? throw new UnauthorizedException();
    public static string? GuestKey(HttpContext context)
    {
        var secret = context.Request.Cookies[CookieName];
        return secret is { Length: 64 } && secret.All(Uri.IsHexDigit) ? CommerceMapping.Hash(secret) : null;
    }
    public static CartOwner Owner(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true) return CartOwner.User(UserId(context.User));
        var key = GuestKey(context);
        if (key is null)
        {
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(CookieName, secret, Options);
            key = CommerceMapping.Hash(secret);
        }
        return CartOwner.Guest(key);
    }
    public static void ClearGuest(HttpContext context) => context.Response.Cookies.Delete(CookieName, Options);
}
