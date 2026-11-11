using TechStore.Infrastructure.Data;

namespace TechStore.API.Hosting;

public sealed class DevelopmentDataSeedingService(IServiceScopeFactory scopeFactory, IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TechStoreDataSeeder>().SeedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
