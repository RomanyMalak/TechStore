using Microsoft.Extensions.Options;
using TechStore.Infrastructure.Data;

namespace TechStore.API.Hosting;

public sealed class GuestCartCleanupService(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    IOptions<GuestCartCleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<GuestCartCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || (environment.IsDevelopment() && !settings.AllowDevelopmentCleanup))
        {
            logger.LogInformation("Guest cart cleanup is disabled for this environment.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoffUtc = timeProvider.GetUtcNow().UtcDateTime.AddDays(-settings.RetentionDays);
                var total = 0;
                for (var batch = 0; batch < settings.MaximumBatchesPerRun; batch++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var deleted = await scope.ServiceProvider.GetRequiredService<IGuestCartCleanup>()
                        .DeleteExpiredBatchAsync(cutoffUtc, settings.BatchSize, stoppingToken);
                    total += deleted;
                    if (deleted < settings.BatchSize) break;
                }
                logger.LogInformation("Guest cart cleanup removed {CartCount} expired guest carts.", total);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Failed maintenance must not stop the API. Retry on the next scheduled pass.
                logger.LogWarning(exception, "Guest cart cleanup failed; it will retry on the next scheduled pass.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.IntervalMinutes), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
