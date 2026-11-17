using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TechStore.Domain.Entities;
using TechStore.Infrastructure.Identity;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests.SqlServer;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerFixture.Enabled)
            Skip = "Opt in with TECHSTORE_RUN_SQL_TESTS=1; tests create and drop their own isolated SQL database.";
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "Isolated SQL Server";
}

public sealed class SqlServerFixture : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("TECHSTORE_RUN_SQL_TESTS") == "1";
    private readonly string databaseName = "TechStore_RelationalTests_" + Guid.NewGuid().ToString("N");
    private string connectionString = string.Empty;
    private string masterConnectionString = string.Empty;
    private bool created;
    // Identity reads this option through the application's provider when building
    // login/token keys. Standalone contexts must use the same setting as AddInfrastructure.
    private readonly ServiceProvider identityModelServices = new ServiceCollection()
        .Configure<IdentityOptions>(options => options.Stores.MaxLengthForKeys = 128).BuildServiceProvider();

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        var configured = Environment.GetEnvironmentVariable("TECHSTORE_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechStore.slnx")))
                directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException("Cannot locate the solution for SQL test configuration.");
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.FullName, "TechStore.API", "appsettings.json")));
            configured = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString();
        }
        // Use server/auth settings only. Never migrate, clear, or delete the configured catalog.
        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "master", ApplicationName = "TechStore isolated relational tests", ConnectTimeout = 15
        };
        masterConnectionString = builder.ConnectionString;
        RequireOwnedDatabaseName();
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
            created = true;
        }
        builder.InitialCatalog = databaseName;
        connectionString = builder.ConnectionString;
        try
        {
            await using var context = Context();
            await context.Database.MigrateAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public void Configure(DbContextOptionsBuilder options) =>
        options.UseSqlServer(connectionString, sql => sql.CommandTimeout(30));

    public void ConfigureProcessEnvironment(IDictionary<string, string?> environment) =>
        environment["ConnectionStrings__DefaultConnection"] = connectionString;

    public ApplicationDbContext Context(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        options.UseApplicationServiceProvider(identityModelServices);
        Configure(options);
        return new(options.AddInterceptors(interceptors).Options);
    }

    public async Task<Scenario> SeedAsync(int stock = 10, bool cart = true)
    {
        await using var context = Context();
        var category = new Category();
        category.UpdateDetails("SQL category " + Guid.NewGuid().ToString("N"), "Isolated test", null, true);
        var user = NewUser();
        context.Categories.Add(category);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var product = Product.Create("SQL product", "Isolated test product", Guid.NewGuid().ToString("N"),
            "Test brand", 100m, 90m, stock, category.Id, true);
        context.Products.Add(product);
        await context.SaveChangesAsync();
        if (cart)
        {
            context.Carts.Add(new Cart { UserId = user.Id, Items = [new CartItem { ProductId = product.Id, Quantity = 1 }] });
            await context.SaveChangesAsync();
        }
        return new(user.Id, product.Id, stock);
    }

    public async Task<string> AddCustomerCartAsync(int productId)
    {
        await using var context = Context();
        var user = NewUser();
        context.Users.Add(user);
        context.Carts.Add(new Cart { UserId = user.Id, Items = [new CartItem { ProductId = productId, Quantity = 1 }] });
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static ApplicationUser NewUser()
    {
        var email = Guid.NewGuid().ToString("N") + "@isolated.example.test";
        return new ApplicationUser { FullName = "SQL test customer", UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant() };
    }

    public async Task DisposeAsync()
    {
        await identityModelServices.DisposeAsync();
        if (!created) return;
        RequireOwnedDatabaseName();
        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
        created = false;
    }

    private void RequireOwnedDatabaseName()
    {
        const string prefix = "TechStore_RelationalTests_";
        if (!databaseName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(databaseName[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing to operate on a database not owned by this test fixture.");
    }

    public sealed record Scenario(string UserId, int ProductId, int InitialStock);
}
