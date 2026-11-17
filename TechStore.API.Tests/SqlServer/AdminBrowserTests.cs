using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace TechStore.API.Tests.SqlServer;

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (!SqlServerFixture.Enabled || Environment.GetEnvironmentVariable("TECHSTORE_RUN_BROWSER_TESTS") != "1")
            Skip = "Opt in with TECHSTORE_RUN_SQL_TESTS=1 and TECHSTORE_RUN_BROWSER_TESTS=1; requires Chrome and the Angular production build.";
    }
}

// Browser catalog assertions require a fresh catalog, independent of relational scenario order.
public sealed partial class AdminBrowserTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly SqlServerFixture database = new();
    private readonly string imageRunId = Guid.NewGuid().ToString("N");
    private string ImageParent => Path.GetFullPath(Path.Combine(SolutionRoot(), "Frontend", "TechStore.Client", "artifacts", "browser-image"));
    private string ImageRoot => Path.Combine(ImageParent, imageRunId);
    private string ImageStorage => Path.Combine(ImageRoot, "storage");
    public Task InitializeAsync() => database.InitializeAsync();
    public async Task DisposeAsync()
    {
        try { await database.DisposeAsync(); }
        finally { DeleteOwnedImageDirectory(); }
    }

    [BrowserFact]
    public async Task ProductionAdminBrowser_RealApisAndSql_RoleAwareLoginAndManagement()
    {
        var root = SolutionRoot();
        // Verification can build into an isolated artifact directory while the
        // developer's current API keeps running with its existing binaries.
        var apiDll = Environment.GetEnvironmentVariable("TECHSTORE_E2E_API_DLL")
            ?? Path.Combine(root, "TechStore.API", "bin", "Release", "net8.0", "TechStore.API.dll");
        var frontend = Path.Combine(root, "Frontend", "TechStore.Client");
        Assert.True(File.Exists(Path.Combine(frontend, "dist", "TechStore.Client", "server", "server.mjs")), "Build Angular production before browser verification.");
        var apiPort = FreePort(); var frontendPort = FreePort();
        var apiOrigin = "http://127.0.0.1:" + apiPort;
        var frontendOrigin = "http://localhost:" + frontendPort;
        var email = Guid.NewGuid().ToString("N") + "@browser.example.test";
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        var imageInputs = CreateImageInputs();
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
            environment["DevelopmentAdmin__Email"] = email;
            environment["DevelopmentAdmin__Password"] = password;
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
            using (var ssrClient = new HttpClient())
            {
                var html = await ssrClient.GetStringAsync(frontendOrigin + "/products");
                Assert.Contains("Lenovo ThinkPad", html);
                Assert.Contains("ngh=", html); // Actual server HTML + hydration, not an empty CSR fallback.
                using var forgedHost = new HttpRequestMessage(HttpMethod.Get, frontendOrigin + "/products");
                forgedHost.Headers.Host = "attacker.invalid";
                using var rejected = await ssrClient.SendAsync(forgedHost);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            var order = await ArrangeOrderAsync(apiOrigin);
            int[] expectedDashboard;
            await using (var snapshot = database.Context())
                expectedDashboard = [await snapshot.Products.CountAsync(product => !product.IsDeleted),
                    await snapshot.Products.CountAsync(product => !product.IsDeleted && product.IsActive),
                    await snapshot.Categories.CountAsync(), await snapshot.Orders.CountAsync(),
                    await snapshot.Orders.CountAsync(item => item.Status == TechStore.Domain.Enums.OrderStatus.Pending),
                    await snapshot.Products.CountAsync(product => !product.IsDeleted && product.Stock > 0)];
            browser = Start("node", frontend, ["tests/browser/admin-journey.mjs"], environment =>
            {
                environment["TECHSTORE_E2E_ORIGIN"] = frontendOrigin;
                environment["TECHSTORE_E2E_ADMIN_EMAIL"] = email;
                environment["TECHSTORE_E2E_ADMIN_PASSWORD"] = password;
                environment["TECHSTORE_E2E_ORDER_ID"] = order.Id.ToString();
                environment["TECHSTORE_E2E_NEW_ADMIN_EMAIL"] = Guid.NewGuid().ToString("N") + "@browser.example.test";
                environment["TECHSTORE_E2E_NEW_ADMIN_PASSWORD"] = Guid.NewGuid().ToString("N") + "Aa1!";
                environment["TECHSTORE_E2E_CUSTOMER_EMAIL"] = order.Email;
                environment["TECHSTORE_E2E_CUSTOMER_PASSWORD"] = order.Password;
                environment["TECHSTORE_E2E_CUSTOMER_TOKEN"] = order.Token;
                environment["TECHSTORE_E2E_CUSTOMER_JOURNEY"] = "1";
                environment["TECHSTORE_E2E_IMAGE_PNG"] = imageInputs.Png;
                environment["TECHSTORE_E2E_IMAGE_JPEG"] = imageInputs.Jpeg;
                environment["TECHSTORE_E2E_IMAGE_WEBP"] = imageInputs.Webp;
            });
            var standardOutput = browser.StandardOutput.ReadToEndAsync();
            var standardError = browser.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await browser.WaitForExitAsync(timeout.Token);
            var report = await standardOutput;
            output.WriteLine(report);
            Assert.True(browser.ExitCode == 0, "Real browser verification failed: " + await standardError);
            using var verified = JsonDocument.Parse(report);
            Assert.True(verified.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(expectedDashboard, verified.RootElement.GetProperty("dashboardValues").EnumerateArray().Select(value => value.GetInt32()).ToArray());
            var productId = verified.RootElement.GetProperty("managedProductId").GetInt32();
            await using var context = database.Context();
            var product = (await context.Products.FindAsync(productId))!;
            Assert.True(product.IsDeleted);
            Assert.Equal(5, product.Stock);
            Assert.EndsWith("updated", product.Name);
            Assert.Equal(TechStore.Domain.Enums.OrderStatus.Cancelled, (await context.Orders.FindAsync(order.Id))!.Status);
            Assert.Equal(3, await context.OrderStatusHistories.CountAsync(entry => entry.OrderId == order.Id));
            var recoveredId=verified.RootElement.GetProperty("recoveredOrderId").GetInt32();
            var recovered=(await context.Orders.FindAsync(recoveredId))!;
            Assert.Equal(TechStore.Domain.Enums.OrderStatus.Cancelled,recovered.Status);
            Assert.Equal(2,await context.Orders.CountAsync(item=>item.UserId==recovered.UserId));
            Assert.Equal(2,await context.OrderStatusHistories.CountAsync(entry=>entry.OrderId==recoveredId));
            await AssertImageUploadAsync(verified.RootElement.GetProperty("imageUpload"), productId, context);
        }
        finally
        {
            Stop(browser); Stop(server); Stop(api);
        }
    }

    private async Task AssertImageUploadAsync(JsonElement report, int productId, ApplicationDbContext context)
    {
        Assert.True(report.GetProperty("passed").GetBoolean());
        Assert.Equal(new[] { "PNG", "JPEG", "WEBP" }, report.GetProperty("inputFormats").EnumerateArray().Select(value => value.GetString()).ToArray());
        var createdUrls = report.GetProperty("createdImageUrls").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var finalUrls = report.GetProperty("finalImageUrls").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var discarded = report.GetProperty("discardedDraftUrl").GetString()!;
        var removed = report.GetProperty("removedImageUrl").GetString()!;
        var primary = report.GetProperty("finalPrimaryImageUrl").GetString()!;
        Assert.Equal(2, createdUrls.Length);
        Assert.Equal(2, finalUrls.Length);
        Assert.Equal(2, finalUrls.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(primary, finalUrls);
        Assert.DoesNotContain(removed, finalUrls);
        Assert.DoesNotContain(discarded, finalUrls);
        foreach (var url in createdUrls.Concat(finalUrls).Append(discarded).Append(removed))
            Assert.True(ProductImagePolicy.TryGetManagedId(url, out _), "Browser report returned a noncanonical uploaded image URL.");
        Assert.Equal(new[] { 0, 1 }, report.GetProperty("finalSortOrders").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.True(report.GetProperty("storefrontCatalogVerified").GetBoolean());
        Assert.True(report.GetProperty("storefrontDetailsVerified").GetBoolean());
        var progress = report.GetProperty("progress");
        Assert.True(progress.GetProperty("xhrEventSeen").GetBoolean());
        Assert.Equal(100, progress.GetProperty("percentage").GetInt32());
        Assert.True(progress.GetProperty("saveDisabled").GetBoolean());
        Assert.True(progress.GetProperty("previewVisible").GetBoolean());
        Assert.InRange(progress.GetProperty("maximumConcurrentRequests").GetInt32(), 1, 2);
        var rows = await context.ProductImages.AsNoTracking().Where(image => image.ProductId == productId)
            .OrderBy(image => image.SortOrder).ToArrayAsync();
        Assert.Equal(finalUrls, rows.Select(image => image.ImageUrl));
        Assert.Equal(new[] { 0, 1 }, rows.Select(image => image.SortOrder));
        Assert.Equal(primary, Assert.Single(rows, image => image.IsPrimary).ImageUrl);
        foreach (var url in finalUrls)
        {
            var bytes = await File.ReadAllBytesAsync(ImagePath(url));
            Assert.InRange(bytes.LongLength, 1, ProductImagePolicy.MaximumUploadBytes);
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            using var decoded = SKBitmap.Decode(bytes);
            Assert.NotNull(decoded);
            Assert.InRange(decoded.Width, 1, ProductImagePolicy.MaximumDimension);
            Assert.InRange(decoded.Height, 1, ProductImagePolicy.MaximumDimension);
        }
        Assert.False(File.Exists(ImagePath(removed)));
        Assert.False(File.Exists(ImagePath(discarded)));
        Assert.Equal(2, Directory.GetFiles(ImageStorage, "*.webp", SearchOption.AllDirectories).Length);
    }

    private string ImagePath(string url)
    {
        if (!ProductImagePolicy.TryGetManagedId(url, out var id)) throw new InvalidOperationException("An image URL is outside the managed namespace.");
        var name = id.ToString("N");
        return Path.Combine(ImageStorage, "files", name[..2], name + ".webp");
    }

    private (string Png, string Jpeg, string Webp) CreateImageInputs()
    {
        var inputs = Path.Combine(ImageRoot, "inputs");
        Directory.CreateDirectory(inputs);
        return (WriteImageInput(inputs, "upload.png", SKEncodedImageFormat.Png, 768, 512),
            WriteImageInput(inputs, "upload.jpeg", SKEncodedImageFormat.Jpeg, 96, 64),
            WriteImageInput(inputs, "upload.webp", SKEncodedImageFormat.Webp, 96, 64));
    }

    private static string WriteImageInput(string directory, string name, SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(0x54454348);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        Assert.NotNull(encoded);
        Assert.InRange(encoded.Size, 1, ProductImagePolicy.MaximumUploadBytes);
        var path = Path.GetFullPath(Path.Combine(directory, name));
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    private void DeleteOwnedImageDirectory()
    {
        var path = Path.GetFullPath(ImageRoot);
        if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _)
            || !string.Equals(Path.GetDirectoryName(path), ImageParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to clean an image directory not owned by this browser test.");
        if (!Directory.Exists(path)) return;
        for (string? ancestor = path; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to clean browser image storage through a reparse point.");
        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.TryPop(out var current))
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to recursively clean a browser image reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        Directory.Delete(path, recursive: true);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechStore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate solution.");
    }

    private static Process Start(string file, string workingDirectory, string[] arguments, Action<IDictionary<string, string?>> configure)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        configure(start.Environment);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start verification process.");
        // API/frontend output is drained without printing configuration or test secrets.
        if (arguments[0] != "tests/browser/admin-journey.mjs" && arguments[0] != "tests/browser/analytics-runner.mjs" && arguments[0] != "tests/browser/i18n-journey.mjs")
        {
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
        }
        return process;
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        process.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static async Task ReadyAsync(string url, Process process)
    {
        using var client = new HttpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (process.HasExited) throw new InvalidOperationException("Verification server stopped before becoming ready.");
            try { if ((await client.GetAsync(url)).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Verification server did not become ready.");
    }

    private static async Task<(int Id, string Email, string Password, string Token)> ArrangeOrderAsync(string origin)
    {
        using var client = new HttpClient { BaseAddress = new Uri(origin) };
        var email = Guid.NewGuid().ToString("N") + "@browser.example.test";
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        (await client.PostAsJsonAsync("/api/auth/register", new RegisterDto { FullName = "Browser customer", Email = email, Password = password, ConfirmPassword = password })).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password });
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var products = (await client.GetFromJsonAsync<TechStore.Application.Common.PagedResult<TechStore.Application.DTOs.Products.ProductListDto>>("/api/products?InStockOnly=true&PageSize=1"))!;
        (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = products.Items[0].Id })).EnsureSuccessStatusCode();
        var quote = (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;
        var response = await client.PostAsJsonAsync("/api/orders", new CreateOrderDto { ClientRequestId = Guid.NewGuid(), QuoteVersion = quote.Version,
            ShippingAddress = new() { FullName = "Browser customer", Phone = "01012345678", City = "Cairo", Street = "Isolated browser street", Building = "1" } });
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<OrderResponseDto>())!.Id, email, password, auth.AccessToken);
    }
}
