using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TechStore.Application.DTOs.Auth;

namespace TechStore.API.Tests.SqlServer;

public sealed partial class AdminBrowserTests
{
    [BrowserFact]
    public async Task ProductionCheckoutRegressionBrowser_SequentialPurchasesRefreshAndIdempotency()
    {
        var root = SolutionRoot();
        var apiDll = Environment.GetEnvironmentVariable("TECHSTORE_E2E_API_DLL")
            ?? Path.Combine(root, "TechStore.API", "bin", "Release", "net8.0", "TechStore.API.dll");
        var frontend = Path.Combine(root, "Frontend", "TechStore.Client");
        Assert.True(File.Exists(apiDll), "Build the isolated verification API before browser verification.");
        Assert.True(File.Exists(Path.Combine(frontend, "dist", "TechStore.Client", "server", "server.mjs")),
            "Build Angular production before browser verification.");
        var apiOrigin = "http://127.0.0.1:" + FreePort();
        var frontendPort = FreePort();
        var frontendOrigin = "http://localhost:" + frontendPort;
        var adminEmail = Guid.NewGuid().ToString("N") + "@checkout-browser.example.test";
        var adminPassword = Guid.NewGuid().ToString("N") + "Aa1!";
        using var api = Start("dotnet", Path.Combine(root, "TechStore.API"), [apiDll], environment =>
        {
            // The shared fixture creates/drops only its own GUID-named SQL database.
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
        Process? journey = null;
        try
        {
            await ReadyAsync(apiOrigin + "/api/products", api);
            await ReadyAsync(frontendOrigin + "/api/products", server);
            var customerEmail = Guid.NewGuid().ToString("N") + "@checkout-browser.example.test";
            var customerPassword = Guid.NewGuid().ToString("N") + "Aa1!";
            using var client = new HttpClient { BaseAddress = new Uri(apiOrigin) };
            (await client.PostAsJsonAsync("/api/auth/register", new RegisterDto
            {
                FullName = "Isolated checkout customer", Email = customerEmail,
                Password = customerPassword, ConfirmPassword = customerPassword
            })).EnsureSuccessStatusCode();
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = customerEmail, Password = customerPassword });
            login.EnsureSuccessStatusCode();
            var customer = (await login.Content.ReadFromJsonAsync<AuthResponseDto>())!;
            await using (var before = database.Context())
            {
                Assert.Equal(5, await before.Products.CountAsync(product => !product.IsDeleted));
                Assert.Equal(0, await before.Orders.CountAsync(order => order.UserId == customer.User.Id));
            }

            // Use a dedicated process helper: shared server startup deliberately drains its own logs.
            journey = StartCheckoutRegressionJourney(frontend, environment =>
            {
                environment["TECHSTORE_E2E_ORIGIN"] = frontendOrigin;
                environment["TECHSTORE_E2E_CUSTOMER_EMAIL"] = customerEmail;
                environment["TECHSTORE_E2E_CUSTOMER_PASSWORD"] = customerPassword;
                environment["TECHSTORE_E2E_ARTIFACT_DIR"] = Path.Combine(frontend, "artifacts", "checkout-regression-browser", imageRunId);
                // The optional first diagnostic run can precede the requested UI polish.
                environment["TECHSTORE_E2E_VERIFY_SUCCESS_BUTTONS"] =
                    Environment.GetEnvironmentVariable("TECHSTORE_E2E_VERIFY_SUCCESS_BUTTONS") ?? "1";
            });
            var standardOutput = journey.StandardOutput.ReadToEndAsync();
            var standardError = journey.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await journey.WaitForExitAsync(timeout.Token);
            var report = await standardOutput;
            output.WriteLine(report); // The journey emits only sanitized checks and evidence.
            Assert.True(journey.ExitCode == 0, "Checkout browser regression failed: " + await standardError);
            using var verified = JsonDocument.Parse(report);
            var result = verified.RootElement;
            Assert.True(result.GetProperty("passed").GetBoolean());
            Assert.True(result.GetProperty("duplicatePreservedStock").GetBoolean());
            Assert.True(result.GetProperty("failurePreservedCart").GetBoolean());
            Assert.True(result.GetProperty("guestMergeVerified").GetBoolean());
            Assert.True(result.GetProperty("storageRemovalFailureVerified").GetBoolean());
            if (Environment.GetEnvironmentVariable("TECHSTORE_E2E_VERIFY_SUCCESS_BUTTONS") != "0")
                Assert.True(result.GetProperty("successButtonsVerified").GetBoolean());

            // Verify browser claims again directly in the disposable SQL database using decimal arithmetic.
            var purchases = result.GetProperty("purchases").EnumerateArray().ToArray();
            Assert.Equal(3, purchases.Length);
            var expectedIds = purchases.Select(order => order.GetProperty("id").GetInt32()).Order().ToArray();
            await using var context = database.Context();
            var orders = await context.Orders.Include(order => order.Items)
                .Where(order => order.UserId == customer.User.Id).OrderBy(order => order.Id).ToArrayAsync();
            Assert.Equal(expectedIds, orders.Select(order => order.Id));
            Assert.Equal(3, orders.Select(order => order.ClientRequestId).Distinct().Count());
            foreach (var order in orders)
            {
                var observed = purchases.Single(item => item.GetProperty("id").GetInt32() == order.Id);
                Assert.Equal(Guid.Parse(observed.GetProperty("clientRequestId").GetString()!), order.ClientRequestId);
                Assert.Equal(observed.GetProperty("subTotal").GetDecimal(), order.SubTotal);
                Assert.Equal(observed.GetProperty("shippingCost").GetDecimal(), order.ShippingCost);
                Assert.Equal(observed.GetProperty("taxAmount").GetDecimal(), order.TaxAmount);
                Assert.Equal(observed.GetProperty("totalAmount").GetDecimal(), order.TotalAmount);
                Assert.Equal(order.Items.Sum(item => item.LineTotal), order.SubTotal);
                Assert.Equal(order.SubTotal + order.ShippingCost + order.TaxAmount, order.TotalAmount);
                foreach (var line in order.Items)
                {
                    var product = await context.Products.SingleAsync(product => product.Id == line.ProductId);
                    Assert.Equal(product.DiscountPrice ?? product.Price, line.UnitPrice);
                    Assert.Equal(line.UnitPrice * line.Quantity, line.LineTotal);
                }
            }
            var carts = await context.Carts.Include(cart => cart.Items).ToArrayAsync();
            Assert.All(carts, cart => Assert.Empty(cart.Items));
            foreach (var initial in result.GetProperty("initialStock").EnumerateArray())
            {
                var productId = initial.GetProperty("id").GetInt32();
                var initialStock = initial.GetProperty("stock").GetInt32();
                var purchased = orders.SelectMany(order => order.Items).Where(item => item.ProductId == productId).Sum(item => item.Quantity);
                Assert.Equal(initialStock - purchased, await context.Products.Where(product => product.Id == productId).Select(product => product.Stock).SingleAsync());
            }
        }
        finally { Stop(journey); Stop(server); Stop(api); }
    }

    private static Process StartCheckoutRegressionJourney(string frontend, Action<IDictionary<string, string?>> configure)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("tests/browser/checkout-regression-journey.mjs");
        configure(start.Environment);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start checkout browser regression.");
    }
}
