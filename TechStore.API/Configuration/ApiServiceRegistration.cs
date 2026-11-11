using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using TechStore.API.Errors;
using TechStore.API.Hosting;
using TechStore.API.OpenApi;
using TechStore.Infrastructure.Authentication;

namespace TechStore.API.Configuration;

public static class ApiServiceRegistration
{
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddApiRateLimiting();
        services.AddTrustedProxyForwarding();
        services.AddControllers().ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = actionContext =>
            {
                var problem = ApiProblemWriter.Enrich(actionContext.HttpContext, new ValidationProblemDetails(actionContext.ModelState)
                {
                    Status = StatusCodes.Status400BadRequest, Title = "One or more validation errors occurred.", Type = "about:blank"
                });
                var result = new BadRequestObjectResult(problem);
                result.ContentTypes.Add("application/problem+json");
                return result;
            };
        });
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.IncludeErrorDetails = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                    ValidateAudience = true, ValidAudience = jwt.Audience,
                    ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                    NameClaimType = JwtRegisteredClaimNames.Sub, RoleClaimType = "role", ClockSkew = TimeSpan.FromSeconds(30)
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
                        return ApiProblemWriter.WriteAsync(context.HttpContext,
                            ApiProblemWriter.Create(context.HttpContext, StatusCodes.Status401Unauthorized, "Authentication is required or the token is invalid."),
                            context.HttpContext.RequestAborted);
                    },
                    OnForbidden = context => ApiProblemWriter.WriteAsync(context.HttpContext,
                        ApiProblemWriter.Create(context.HttpContext, StatusCodes.Status403Forbidden, "You do not have permission to perform this operation."),
                        context.HttpContext.RequestAborted)
                };
            });
        services.AddAuthorization();
        services.AddHostedService<IdentityInitializationService>();
        // The schema is applied with EF migrations before startup; Identity initialization runs first.
        services.AddHostedService<DevelopmentDataSeedingService>();
        services.AddHostedService<GuestCartCleanupService>();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "TechStore API", Version = "v1" });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Log in at /api/auth/login, then paste accessToken here without the Bearer prefix."
            });
            options.OperationFilter<AuthorizationOperationFilter>();
            options.SchemaFilter<SupportedOrderPaymentSchemaFilter>();
        });
        return services;
    }
}
