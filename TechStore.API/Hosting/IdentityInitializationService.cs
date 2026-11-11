using TechStore.Infrastructure.Identity;

namespace TechStore.API.Hosting;

public sealed class IdentityInitializationService(IServiceScopeFactory scopeFactory, IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
        await seeder.SeedRolesAsync(cancellationToken);
        if (environment.IsDevelopment()) await seeder.SeedDevelopmentAdminAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
