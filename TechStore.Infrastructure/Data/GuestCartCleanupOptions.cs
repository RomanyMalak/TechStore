namespace TechStore.Infrastructure.Data;

public sealed class GuestCartCleanupOptions
{
    public const string SectionName = "GuestCartCleanup";
    public const int MaximumBatchSize = 1000;

    // Deletion is opt-in, including Production. Never enable this on an existing
    // Development database without approving the retention operation first.
    public bool Enabled { get; set; }
    public bool AllowDevelopmentCleanup { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int IntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 200;
    public int MaximumBatchesPerRun { get; set; } = 10;
}
