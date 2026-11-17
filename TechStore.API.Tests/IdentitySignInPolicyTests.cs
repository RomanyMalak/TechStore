using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TechStore.Application.DTOs.Auth;
using TechStore.Infrastructure.Identity;

namespace TechStore.API.Tests;

public class IdentitySignInPolicyTests
{
    [Theory]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("account")]
    public async Task ConfirmationRequirements_AreEnforcedBeforeIssuingJwt(string requirement)
    {
        using var factory = new ApiFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<IdentityOptions>(options =>
            {
                options.SignIn.RequireConfirmedEmail = requirement == "email";
                options.SignIn.RequireConfirmedPhoneNumber = requirement == "phone";
                options.SignIn.RequireConfirmedAccount = requirement == "account";
            })));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var registration = AuthenticationTests.Registration();
        var created = await client.PostAsJsonAsync("/api/auth/register", registration);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var login = new LoginDto { Email = registration.Email, Password = registration.Password };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", login)).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByEmailAsync(registration.Email))!;
            if (requirement == "phone")
            {
                user.PhoneNumber = "+201000000000";
                user.PhoneNumberConfirmed = true;
            }
            else user.EmailConfirmed = true;
            Assert.True((await manager.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", login)).StatusCode);
    }

    [Fact]
    public async Task EnabledTwoFactorAuthentication_IsNotBypassedByPasswordOnlyLogin()
    {
        using var factory = new ApiFactory();
        using var client = factory.Client();
        var registration = AuthenticationTests.Registration();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", registration)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByEmailAsync(registration.Email))!;
            Assert.True((await manager.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = registration.Email, Password = registration.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CustomAccountConfirmationPolicy_IsNotBypassed()
    {
        using var factory = new ApiFactory();
        var confirmation = new Mock<IUserConfirmation<ApplicationUser>>();
        confirmation.Setup(policy => policy.IsConfirmedAsync(It.IsAny<UserManager<ApplicationUser>>(), It.IsAny<ApplicationUser>())).ReturnsAsync(false);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Configure<IdentityOptions>(options => options.SignIn.RequireConfirmedAccount = true);
            services.RemoveAll<IUserConfirmation<ApplicationUser>>();
            services.AddScoped(_ => confirmation.Object);
        }));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = ApiFactory.AdminEmail, Password = factory.AdminPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        confirmation.Verify(policy => policy.IsConfirmedAsync(It.IsAny<UserManager<ApplicationUser>>(), It.IsAny<ApplicationUser>()), Times.Once);
    }
}
