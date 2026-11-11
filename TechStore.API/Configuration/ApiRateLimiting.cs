using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TechStore.API.Errors;

namespace TechStore.API.Configuration;

public sealed class ApiRateLimitSettings
{
    public int LoginPermitLimit { get; set; } = 30;
    public int LoginWindowSeconds { get; set; } = 60;
    public int RegistrationPermitLimit { get; set; } = 20;
    public int RegistrationWindowSeconds { get; set; } = 600;
    public int GuestCartPermitLimit { get; set; } = 60;
    public int CustomerCartPermitLimit { get; set; } = 120;
    public int CartWindowSeconds { get; set; } = 60;
    public int SensitiveWritePermitLimit { get; set; } = 60;
    public int SensitiveWriteWindowSeconds { get; set; } = 60;

    public bool IsValid() => new[] { LoginPermitLimit, RegistrationPermitLimit, GuestCartPermitLimit,
        CustomerCartPermitLimit, SensitiveWritePermitLimit }.All(value => value is > 0 and <= 100_000)
        && new[] { LoginWindowSeconds, RegistrationWindowSeconds, CartWindowSeconds, SensitiveWriteWindowSeconds }
            .All(value => value is > 0 and <= 86_400);
}

public static class ApiRateLimiting
{
    public const string Login = "login";
    public const string Registration = "registration";
    public const string Cart = "cart";
    public const string SensitiveWrite = "sensitive-write";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<ApiRateLimitSettings>().BindConfiguration("RateLimiting")
            .Validate(settings => settings.IsValid(), "Rate limits and windows must be positive and within supported bounds.")
            .ValidateOnStart();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ApiRateLimitSettings>>((options, configured) =>
        {
            var limits = configured.Value;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                var response = context.HttpContext.Response;
                response.Headers.CacheControl = "no-store";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                await ApiProblemWriter.WriteAsync(context.HttpContext,
                    ApiProblemWriter.Create(context.HttpContext, StatusCodes.Status429TooManyRequests,
                        "Too many requests. Try again later."), token);
            };
            options.AddPolicy(Login, context => Window(Ip(context), limits.LoginPermitLimit, limits.LoginWindowSeconds));
            options.AddPolicy(Registration, context => Window(Ip(context), limits.RegistrationPermitLimit, limits.RegistrationWindowSeconds));
            // An untrusted guest cookie never creates a new rate-limit bucket. Authenticated carts use the verified subject.
            options.AddPolicy(Cart, context => Window(Owner(context),
                context.User.Identity?.IsAuthenticated == true ? limits.CustomerCartPermitLimit : limits.GuestCartPermitLimit,
                limits.CartWindowSeconds));
            options.AddPolicy(SensitiveWrite, context => Window(Owner(context), limits.SensitiveWritePermitLimit,
                limits.SensitiveWriteWindowSeconds));
        });
        return services;
    }

    private static string Ip(HttpContext context) => "ip:" + (context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown");
    private static string Owner(HttpContext context) => context.User.Identity?.IsAuthenticated == true
        && context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { Length: > 0 } subject ? "user:" + subject : Ip(context);
    private static RateLimitPartition<string> Window(string key, int permits, int seconds) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = TimeSpan.FromSeconds(seconds), QueueLimit = 0, AutoReplenishment = true
        });
}
