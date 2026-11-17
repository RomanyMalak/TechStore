using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TechStore.API.Tests.SqlServer;

public sealed partial class AdminBrowserTests
{
    [BrowserFact]
    public async Task ProductionBilingualBrowser_RealApisAndIsolatedSql_LanguageStateAndAuthorization()
    {
        var root = SolutionRoot();
        // Use a frozen API build so verification cannot replace a running developer API's files.
        var apiDll = Environment.GetEnvironmentVariable("TECHSTORE_E2E_API_DLL")
            ?? Path.Combine(root, "TechStore.API", "bin", "Release", "net8.0", "TechStore.API.dll");
        var frontend = Path.Combine(root, "Frontend", "TechStore.Client");
        Assert.True(File.Exists(apiDll), "Build the isolated verification API before browser verification.");
        Assert.True(File.Exists(Path.Combine(frontend, "dist", "TechStore.Client", "server", "server.mjs")),
            "Build Angular production before browser verification.");
        Assert.True(File.Exists(Path.Combine(frontend, "tests", "browser", "i18n-journey.mjs")),
            "The bilingual browser journey is required for this verification.");

        var apiOrigin = "http://127.0.0.1:" + FreePort();
        var frontendPort = FreePort();
        var frontendOrigin = "http://localhost:" + frontendPort;
        var adminEmail = Guid.NewGuid().ToString("N") + "@bilingual.example.test";
        var adminPassword = Guid.NewGuid().ToString("N") + "Aa1!";
        using var api = Start("dotnet", Path.Combine(root, "TechStore.API"), [apiDll], environment =>
        {
            // SqlServerFixture creates and later drops only its own GUID-named database.
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

        Process? browser = null;
        try
        {
            await ReadyAsync(apiOrigin + "/api/products", api);
            await ReadyAsync(frontendOrigin + "/api/products", server);
            using (var client = new HttpClient())
            {
                var html = await client.GetStringAsync(frontendOrigin + "/auth/login");
                Assert.Contains("lang=\"ar\"", html);
                Assert.Contains("dir=\"rtl\"", html);
                Assert.Contains("ngh=", html);
            }
            await using (var context = database.Context())
            {
                Assert.Equal(5, await context.Products.CountAsync(product => !product.IsDeleted));
                Assert.True(await context.Products.AnyAsync(product => product.IsActive && !product.IsDeleted && product.Stock > 0));
                Assert.True(await context.Categories.AnyAsync(category => category.IsActive));
            }
            var order = await ArrangeOrderAsync(apiOrigin);
            browser = Start("node", frontend, ["tests/browser/i18n-journey.mjs"], environment =>
            {
                environment["TECHSTORE_E2E_ORIGIN"] = frontendOrigin;
                environment["TECHSTORE_E2E_ADMIN_EMAIL"] = adminEmail;
                environment["TECHSTORE_E2E_ADMIN_PASSWORD"] = adminPassword;
                environment["TECHSTORE_E2E_CUSTOMER_EMAIL"] = order.Email;
                environment["TECHSTORE_E2E_CUSTOMER_PASSWORD"] = order.Password;
                environment["TECHSTORE_E2E_CUSTOMER_TOKEN"] = order.Token;
                environment["TECHSTORE_E2E_ORDER_ID"] = order.Id.ToString();
                environment["TECHSTORE_E2E_ARTIFACT_DIR"] = Path.Combine(frontend, "artifacts", "i18n-browser", imageRunId);
            });
            var standardOutput = browser.StandardOutput.ReadToEndAsync();
            var standardError = browser.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await browser.WaitForExitAsync(timeout.Token);
            var report = await standardOutput;
            output.WriteLine(report);
            Assert.True(browser.ExitCode == 0, "Real bilingual browser verification failed: " + await standardError);
            using var verified = JsonDocument.Parse(report);
            Assert.True(verified.RootElement.GetProperty("passed").GetBoolean());
        }
        finally
        {
            Stop(browser); Stop(server); Stop(api);
        }
    }
}
