using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TechStore.API.Hosting;
using TechStore.Infrastructure;
using TechStore.Infrastructure.Data;
using TechStore.Infrastructure.Persistence;

namespace TechStore.API.Tests;

public class GuestCartCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, "Production", false, 0)]
    [InlineData(false, "Development", true, 0)]
    [InlineData(true, "Development", false, 0)]
    [InlineData(true, "Development", true, 1)]
    [InlineData(true, "Production", false, 1)]
    public async Task Cleanup_RequiresExplicitEnablement_AndExtraDevelopmentConsent(
        bool enabled, string environment, bool developmentConsent, int expectedCalls)
    {
        var cleanup = new Mock<IGuestCartCleanup>(MockBehavior.Strict);
        cleanup.Setup(service => service.DeleteExpiredBatchAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        using var provider = Provider(cleanup.Object);
        using var host = Host(provider, new GuestCartCleanupOptions
        {
            Enabled = enabled, AllowDevelopmentCleanup = developmentConsent
        }, environment);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        cleanup.Verify(service => service.DeleteExpiredBatchAsync(
            Now.UtcDateTime.AddDays(-30), 200, It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
        Assert.Equal(expectedCalls, cleanup.Invocations.Count);
    }

    [Fact]
    public async Task FullBatches_AreBounded_AndUseOneUtcCutoff()
    {
        var cleanup = new Mock<IGuestCartCleanup>(MockBehavior.Strict);
        cleanup.Setup(service => service.DeleteExpiredBatchAsync(Now.UtcDateTime.AddDays(-30), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        using var provider = Provider(cleanup.Object);
        using var host = Host(provider, new GuestCartCleanupOptions { Enabled = true, BatchSize = 2, MaximumBatchesPerRun = 3 });

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(3, cleanup.Invocations.Count);
    }

    [Fact]
    public async Task ShortBatch_StopsThePassWithoutUnnecessaryQueries()
    {
        var cleanup = new Mock<IGuestCartCleanup>(MockBehavior.Strict);
        cleanup.SetupSequence(service => service.DeleteExpiredBatchAsync(It.IsAny<DateTime>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2).ReturnsAsync(1);
        using var provider = Provider(cleanup.Object);
        using var host = Host(provider, new GuestCartCleanupOptions { Enabled = true, BatchSize = 2, MaximumBatchesPerRun = 10 });

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(2, cleanup.Invocations.Count);
    }

    [Fact]
    public async Task MaintenanceFailure_DoesNotStopTheApi_OrImmediatelyRetryInALoop()
    {
        var cleanup = new Mock<IGuestCartCleanup>(MockBehavior.Strict);
        cleanup.Setup(service => service.DeleteExpiredBatchAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Isolated maintenance failure"));
        using var provider = Provider(cleanup.Object);
        using var host = Host(provider, new GuestCartCleanupOptions { Enabled = true });

        await host.StartAsync(CancellationToken.None);
        Assert.False(host.ExecuteTask!.IsCompleted);
        await host.StopAsync(CancellationToken.None);

        Assert.Single(cleanup.Invocations);
        Assert.True(host.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Shutdown_CancelsAnInFlightDelete()
    {
        var cleanup = new Mock<IGuestCartCleanup>(MockBehavior.Strict);
        CancellationToken receivedToken = default;
        cleanup.Setup(service => service.DeleteExpiredBatchAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (DateTime _, int _, CancellationToken token) =>
            {
                receivedToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            });
        using var provider = Provider(cleanup.Object);
        using var host = Host(provider, new GuestCartCleanupOptions { Enabled = true });

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.True(receivedToken.IsCancellationRequested);
        Assert.True(host.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("RetentionDays", "0")]
    [InlineData("RetentionDays", "3651")]
    [InlineData("IntervalMinutes", "0")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("MaximumBatchesPerRun", "0")]
    public void InvalidRetentionConfiguration_IsRejectedBeforeMaintenanceStarts(string name, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=unused;Integrated Security=true",
            [$"GuestCartCleanup:{name}"] = value
        }).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GuestCartCleanupOptions>>().Value);
    }

    [Fact]
    public async Task InvalidBatchOrLocalCutoff_CannotReachDatabaseDeletion()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var cleanup = new GuestCartCleanup(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cleanup.DeleteExpiredBatchAsync(Now.UtcDateTime, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => cleanup.DeleteExpiredBatchAsync(DateTime.SpecifyKind(Now.DateTime, DateTimeKind.Unspecified), 10));
    }

    private static ServiceProvider Provider(IGuestCartCleanup cleanup) => new ServiceCollection()
        .AddScoped(_ => cleanup).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static GuestCartCleanupService Host(ServiceProvider provider, GuestCartCleanupOptions options, string environment = "Production") =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), new EnvironmentStub(environment), Options.Create(options),
            new FrozenTimeProvider(), NullLogger<GuestCartCleanupService>.Instance);

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EnvironmentStub(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "GuestCartCleanup.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
