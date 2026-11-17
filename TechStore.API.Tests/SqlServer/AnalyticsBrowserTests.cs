using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Products;
using TechStore.Domain.Entities;
using TechStore.Domain.Enums;

namespace TechStore.API.Tests.SqlServer;

public sealed partial class AdminBrowserTests
{
    [BrowserFact]
    public async Task ProductionAnalyticsBrowser_RealSqlDateFiltersChartsAndAuthorization()
    {
        var root = SolutionRoot();
        var apiDll = Environment.GetEnvironmentVariable("TECHSTORE_E2E_API_DLL")
            ?? Path.Combine(root, "TechStore.API", "bin", "Release", "net8.0", "TechStore.API.dll");
        var frontend = Path.Combine(root, "Frontend", "TechStore.Client");
        var apiOrigin = "http://127.0.0.1:" + FreePort();
        var frontendPort = FreePort();
        var frontendOrigin = "http://localhost:" + frontendPort;
        var adminEmail = Guid.NewGuid().ToString("N") + "@analytics.example.test";
        var adminPassword = Guid.NewGuid().ToString("N") + "Aa1!";
        var inputs = CreateImageInputs();
        using var api = Start("dotnet", Path.Combine(root, "TechStore.API"), [apiDll], environment =>
        {
            database.ConfigureProcessEnvironment(environment);
            environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            environment["ASPNETCORE_URLS"] = apiOrigin;
            environment.Remove("ASPNETCORE_HTTPS_PORT");
            environment.Remove("ASPNETCORE_HTTPS_PORTS");
            environment["Jwt__Issuer"] = "TechStore.Browser.Tests";
            environment["Jwt__Audience"] = "TechStore.Browser.Client";
            environment["Jwt__SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
            environment["DevelopmentAdmin__Email"] = adminEmail;
            environment["DevelopmentAdmin__Password"] = adminPassword;
            environment["GuestCartCleanup__Enabled"] = "false";
            environment["ProductImages__RootPath"] = ImageStorage;
            environment["ProductImages__CleanupEnabled"] = "false";
        });
        using var server = Start("node", frontend, ["dist/TechStore.Client/server/server.mjs"], environment =>
        {
            environment["PORT"] = frontendPort.ToString();
            environment["TECHSTORE_API_ORIGIN"] = apiOrigin;
        });
        System.Diagnostics.Process? browser = null;
        try
        {
            await ReadyAsync(apiOrigin + "/api/products", api);
            await ReadyAsync(frontendOrigin + "/api/products", server);
            using var client = new HttpClient { BaseAddress = new Uri(apiOrigin) };
            var admin = await BrowserLoginAsync(client, adminEmail, adminPassword);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
            using var form = new MultipartFormDataContent();
            using var imageContent = new ByteArrayContent(await File.ReadAllBytesAsync(inputs.Png));
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(imageContent, "file", "analytics.png");
            using var uploadResponse = await client.PostAsync("/api/product-images", form);
            uploadResponse.EnsureSuccessStatusCode();
            var uploaded = (await uploadResponse.Content.ReadFromJsonAsync<ProductImageUploadDto>())!;

            var email = Guid.NewGuid().ToString("N") + "@analytics.example.test";
            var password = Guid.NewGuid().ToString("N") + "Aa1!";
            (await client.PostAsJsonAsync("/api/auth/register", new RegisterDto
            { FullName = "Analytics browser customer", Email = email, Password = password, ConfirmPassword = password })).EnsureSuccessStatusCode();
            var customer = await BrowserLoginAsync(client, email, password);
            await using var context = database.Context();
            var userId = await context.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            var categoryId = await context.Categories.Select(category => category.Id).FirstAsync();
            var now = DateTime.UtcNow;
            var products = Enumerable.Range(0, 21).Select(index => Product.Create("Analytics product " + index.ToString("D2"),
                "Isolated browser analytics fixture", "ANALYTICS-" + index, "Browser brand", 20, 10, 100, categoryId)).ToArray();
            context.Products.AddRange(products);
            await context.SaveChangesAsync();
            for (var index = 0; index < products.Length; index++)
            {
                var product = products[index];
                var age = index < 8 ? 1 : index < 15 ? 10 : 45;
                product.Images.Add(new ProductImage { ImageUrl = uploaded.ImageUrl, IsPrimary = true, SortOrder = 0 });
                context.CartAdditionEvents.Add(CartAdditionEvent.Create(product.Id, index + 1, new string('A', 64),
                    Guid.NewGuid(), now.AddDays(-age)));
                var order = AnalyticsFixtureOrder(userId, product, index + 1, now.AddDays(-age), (OrderStatus)(index % 4 + 1));
                context.Orders.Add(order);
            }
            foreach (var age in new[] { 10, 45, 120 })
                context.CartAdditionEvents.Add(CartAdditionEvent.Create(products[0].Id, 2, new string('B', 64), Guid.NewGuid(), now.AddDays(-age)));
            context.Orders.Add(AnalyticsFixtureOrder(userId, products[0], 30, now.AddDays(-120), OrderStatus.Delivered));
            foreach (var status in new[] { OrderStatus.Pending, OrderStatus.Cancelled, OrderStatus.Returned })
                context.Orders.Add(AnalyticsFixtureOrder(userId, products[0], 90, now.AddDays(-1), status));
            // Retired products must retain their historical contribution.
            products[^1].SoftDelete();
            await context.SaveChangesAsync();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customer.AccessToken);
            var add = new AddToCartDto { ProductId = products[0].Id, Quantity = 2, ClientRequestId = Guid.NewGuid() };
            (await client.PostAsJsonAsync("/api/cart/items", add)).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/cart/items", add)).EnsureSuccessStatusCode();
            (await client.GetAsync("/api/cart")).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync("/api/cart/items/" + products[0].Id, new UpdateCartItemDto { Quantity = 1 })).EnsureSuccessStatusCode();
            (await client.DeleteAsync("/api/cart/items/" + products[0].Id)).EnsureSuccessStatusCode();
            Assert.Equal(25, await context.CartAdditionEvents.CountAsync());
            var expected = new
            {
                periods = new Dictionary<string, object>
                {
                    ["7"] = new { totalOrders = 11, confirmedOrders = 8, salesAmount = 360m, unitsSold = 36,
                        averageOrderValue = 45m, totalCount = 8, topOrderedProductId = products[7].Id },
                    ["30"] = new { totalOrders = 18, confirmedOrders = 15, salesAmount = 1200m, unitsSold = 120,
                        averageOrderValue = 80m, totalCount = 15, topOrderedProductId = products[14].Id },
                    ["90"] = new { totalOrders = 24, confirmedOrders = 21, salesAmount = 2310m, unitsSold = 231,
                        averageOrderValue = 110m, totalCount = 21, topOrderedProductId = products[20].Id },
                    ["0"] = new { totalOrders = 25, confirmedOrders = 22, salesAmount = 2610m, unitsSold = 261,
                        averageOrderValue = 118.64m, totalCount = 21, topOrderedProductId = products[0].Id }
                },
                expectedTopAddedProductId = products[0].Id,
                expectedTopOrderedProductId = products[14].Id,
                expectedImageUrl = uploaded.ImageUrl, expectedImageProductId = products[0].Id
            };
            browser = Start("node", frontend, ["tests/browser/analytics-runner.mjs"], environment =>
            {
                environment["TECHSTORE_E2E_ORIGIN"] = frontendOrigin;
                environment["TECHSTORE_E2E_ADMIN_EMAIL"] = adminEmail;
                environment["TECHSTORE_E2E_ADMIN_PASSWORD"] = adminPassword;
                environment["TECHSTORE_E2E_CUSTOMER_EMAIL"] = email;
                environment["TECHSTORE_E2E_CUSTOMER_PASSWORD"] = password;
                environment["TECHSTORE_E2E_CUSTOMER_TOKEN"] = customer.AccessToken;
                environment["TECHSTORE_E2E_ANALYTICS_EXPECTED"] = JsonSerializer.Serialize(expected);
            });
            var standardOutput = browser.StandardOutput.ReadToEndAsync();
            var standardError = browser.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await browser.WaitForExitAsync(timeout.Token);
            var report = await standardOutput;
            output.WriteLine(report);
            Assert.True(browser.ExitCode == 0, "Real analytics browser verification failed: " + await standardError);
            using var verified = JsonDocument.Parse(report);
            Assert.True(verified.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(25, await context.CartAdditionEvents.CountAsync());
        }
        finally { Stop(browser); Stop(server); Stop(api); }
    }

    private static async Task<AuthResponseDto> BrowserLoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
    }

    private static Order AnalyticsFixtureOrder(string userId, Product product, int quantity, DateTime date, OrderStatus status)
    {
        var order = Order.Place(userId, Guid.NewGuid(), new string('F', 64), "Browser customer", "01012345678",
            "Isolated browser street", 15m, [(product, quantity)]);
        order.OrderDate = date;
        order.Status = status;
        return order;
    }
}
