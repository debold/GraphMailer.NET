namespace GraphMailer.Service.Configuration;

public sealed class PerformanceMetricsOptions
{
    public bool Enabled { get; init; } = true;
    public int MemoryIntervalSeconds { get; init; } = 60;
    public int CpuIntervalSeconds { get; init; } = 60;
    public int DiskIntervalSeconds { get; init; } = 300;
}

public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    public bool Enabled { get; init; } = true;
    public int RetentionDays { get; init; } = 90;
    public int CleanupIntervalHours { get; init; } = 24;
    public PerformanceMetricsOptions PerformanceMetrics { get; init; } = new();

    /// <summary>
    /// Base directory for the SQLite database file.
    /// Default (empty string): Directory.GetCurrentDirectory() – the executable directory in production.
    /// Override in tests to avoid Directory.SetCurrentDirectory race conditions.
    /// </summary>
    public string BasePath { get; init; } = string.Empty;
}
