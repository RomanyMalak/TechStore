using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Auth;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests.SqlServer;

// Real controllers, Identity, services, repositories, middleware and SQL transactions.
// Only the database and test secrets change; the target is the fixture-owned disposable catalog.
public sealed class SqlApiFactory(SqlServerFixture database, IInterceptor? interceptor = null,
    IReadOnlyDictionary<string, string?>? settings = null, CheckoutPolicy? checkoutPolicy = null) : WebApplicationFactory<TechStore.API.Program>
{
    private readonly string adminEmail = Guid.NewGuid().ToString("N") + "@isolated.example.test";
    private readonly string adminPassword = Guid.NewGuid().ToString("N") + "Aa1!";
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "TechStore.SQL.Tests", ["Jwt:Audience"] = "TechStore.SQL.Client",
            ["Jwt:SigningKey"] = signingKey, ["DevelopmentAdmin:Email"] = adminEmail,
            ["DevelopmentAdmin:Password"] = adminPassword, ["GuestCartCleanup:Enabled"] = "false"
        }));
        if (settings is not null)
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            // This immutable singleton is captured during minimal-host startup,
            // before late WebApplicationFactory configuration is applied.
            if (checkoutPolicy is not null)
            {
                services.RemoveAll<CheckoutPolicy>();
                services.AddSingleton(checkoutPolicy);
            }
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                database.Configure(options);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
        });
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    public async Task<(HttpClient Client, UserResponseDto User)> CustomerAsync()
    {
        var client = Client();
        var email = Guid.NewGuid().ToString("N") + "@isolated.example.test";
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterDto
        {
            FullName = "SQL Customer", Email = email, Password = password, ConfirmPassword = password
        });
        response.EnsureSuccessStatusCode();
        var user = (await response.Content.ReadFromJsonAsync<UserResponseDto>())!;
        await AuthenticateAsync(client, email, password);
        return (client, user);
    }

    public async Task<(HttpClient Client, UserResponseDto User)> AdminAsync()
    {
        var client = Client();
        return (client, await AuthenticateAsync(client, adminEmail, adminPassword));
    }

    private static async Task<UserResponseDto> AuthenticateAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return auth.User;
    }
}
