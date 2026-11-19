using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Services;
using TechStore.Application.Validators.Products;
using TechStore.Application.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TechStore.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICartService, CartService>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IAdminAccountService, AdminAccountService>();
        services.AddScoped<ICustomerAccountService, CustomerAccountService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new CheckoutPolicy());
        services.AddValidatorsFromAssemblyContaining<CreateProductDtoValidator>(ServiceLifetime.Scoped);
        return services;
    }
}
