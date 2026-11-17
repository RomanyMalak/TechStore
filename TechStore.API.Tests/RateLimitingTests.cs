using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using TechStore.API.Configuration;
using TechStore.Application.DTOs.Auth;

namespace TechStore.API.Tests;

public class RateLimitingTests
{
    [Fact]
    public async Task Login_LimitsAttemptsByIp_AndReturnsProblemDetailsAndRetryAfter()
    {
        await using var factory = new LimitedFactory(options => options.LoginPermitLimit = 1);
        using var client = factory.Client();
        var dto = new LoginDto { Email = "unknown@example.test", Password = "Unstored-test-value1!" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", dto)).StatusCode);
        var rejected = await client.PostAsJsonAsync("/api/auth/login", dto);
        await AssertLimited(rejected);
        Assert.DoesNotContain(dto.Password, await rejected.Content.ReadAsStringAsync());
        // A login bucket does not consume the registration policy's independent budget.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new { })).StatusCode);
    }

    [Fact]
    public async Task Registration_IsLimited_BeforeRepeatedBodyValidationOrIdentityWork()
    {
        await using var factory = new LimitedFactory(options => options.RegistrationPermitLimit = 1);
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new { })).StatusCode);
        await AssertLimited(await client.PostAsJsonAsync("/api/auth/register", new { }));
    }

    [Fact]
    public async Task GuestCart_CannotBypassIpLimitByChangingCookiesOrSpoofingForwardingHeaders()
    {
        await using var factory = new LimitedFactory(options => options.GuestCartPermitLimit = 1);
        using var first = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/cart")).StatusCode);
        using var second = factory.Client();
        second.DefaultRequestHeaders.Add("Cookie", "__Host-TechStore.Cart=" + new string('A', 64));
        second.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");
        second.DefaultRequestHeaders.Add("X-Real-IP", "203.0.113.21");
        second.DefaultRequestHeaders.Add("Forwarded", "for=203.0.113.22");
        await AssertLimited(await second.GetAsync("/api/cart"));
        // Public catalog browsing is not charged against the guest-cart budget.
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedWrites_ArePartitionedByVerifiedUser_AndAuthorizationStillRunsFirst()
    {
        await using var factory = new LimitedFactory(options => options.SensitiveWritePermitLimit = 1);
        using var firstCustomer = await factory.CustomerClientAsync();
        using var secondCustomer = await factory.CustomerClientAsync();
        // Invalid order requests use no stock, but still consume the authenticated write budget.
        Assert.Equal(HttpStatusCode.BadRequest, (await firstCustomer.PostAsJsonAsync("/api/orders", new { })).StatusCode);
        await AssertLimited(await firstCustomer.PostAsJsonAsync("/api/orders", new { }));
        Assert.Equal(HttpStatusCode.BadRequest, (await secondCustomer.PostAsJsonAsync("/api/orders", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await firstCustomer.PostAsJsonAsync("/api/products", CatalogRequests.Create())).StatusCode);
        using var guest = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/api/products", CatalogRequests.Create())).StatusCode);
    }

    [Fact]
    public async Task AdminCatalogWrites_ShareTheAuthenticatedWriteBudget()
    {
        await using var factory = new LimitedFactory(options => options.SensitiveWritePermitLimit = 1);
        using var client = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/products", CatalogRequests.Create())).StatusCode);
        await AssertLimited(await client.PostAsJsonAsync("/api/categories", new { name = "Category" }));
    }

    [Fact]
    public async Task AuthenticatedCart_UsesItsOwnBudgetInsteadOfTheSharedGuestIpBucket()
    {
        await using var factory = new LimitedFactory(options => { options.GuestCartPermitLimit = 1; options.CustomerCartPermitLimit = 2; });
        using var guest = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/cart")).StatusCode);
        await AssertLimited(await guest.GetAsync("/api/cart"));
        using var customer = await factory.CustomerClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/cart")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/cart")).StatusCode);
        await AssertLimited(await customer.GetAsync("/api/cart"));
    }

    [Fact]
    public async Task TrustedLoopbackProxy_PartitionsByItsSanitizedForwardedClientIp()
    {
        await using var factory = new LimitedFactory(options => options.GuestCartPermitLimit = 1, IPAddress.Loopback);
        using var first = factory.Client();
        first.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        first.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        // X-Forwarded-Proto is deliberately ignored: the existing HTTPS API transport stays HTTPS.
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/cart")).StatusCode);
        await AssertLimited(await first.GetAsync("/api/cart"));
        using var other = factory.Client();
        other.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.11");
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/cart")).StatusCode);
    }

    [Fact]
    public async Task UnknownProxy_CannotCreateNewBucketsWithForwardedIpHeaders()
    {
        await using var factory = new LimitedFactory(options => options.GuestCartPermitLimit = 1, IPAddress.Parse("198.51.100.10"));
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/cart")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.11");
        await AssertLimited(await client.GetAsync("/api/cart"));
    }

    [Fact]
    public async Task Swagger_DocumentsOrderCreated_CodOnlyRequest_AndRateLimitResponses()
    {
        await using var factory = new LimitedFactory(_ => { });
        using var client = factory.Client();
        using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = swagger.RootElement;
        var order = root.GetProperty("paths").GetProperty("/api/orders").GetProperty("post");
        Assert.True(order.GetProperty("responses").TryGetProperty("201", out _));
        Assert.False(order.GetProperty("responses").TryGetProperty("200", out _));
        var rejected = order.GetProperty("responses").GetProperty("429");
        Assert.True(rejected.GetProperty("headers").TryGetProperty("Retry-After", out _));
        var payment = root.GetProperty("components").GetProperty("schemas").GetProperty("CreateOrderDto")
            .GetProperty("properties").GetProperty("paymentMethod");
        Assert.Equal(0, Assert.Single(payment.GetProperty("enum").EnumerateArray()).GetInt32());
    }

    private static async Task AssertLimited(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(429, problem!.Status);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    private sealed class LimitedFactory(Action<ApiRateLimitSettings> configure, IPAddress? peer = null) : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<ApiRateLimitSettings>(options =>
                {
                    // Limit only the policy under test; other real policies stay enabled for sign-in/setup.
                    options.LoginPermitLimit = options.RegistrationPermitLimit = options.GuestCartPermitLimit = 1000;
                    options.CustomerCartPermitLimit = options.SensitiveWritePermitLimit = 1000;
                    options.LoginWindowSeconds = options.RegistrationWindowSeconds = options.CartWindowSeconds
                        = options.SensitiveWriteWindowSeconds = 3600;
                    configure(options);
                });
                if (peer is not null) services.AddSingleton<IStartupFilter>(new PeerAddress(peer));
            });
        }
    }

    private sealed class PeerAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, pipeline) => { context.Connection.RemoteIpAddress = address; return pipeline(context); });
            next(app);
        };
    }
}
