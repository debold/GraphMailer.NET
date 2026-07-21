namespace GraphMailer.Service.Services.Reporting;

/// <summary>Health-check outcome, mirrors the ConfigTool StatusPage severity scale.</summary>
internal enum HealthStatus { Ok, Warning, Error, Unknown }

/// <summary>One row in the report's Health Checks table.</summary>
internal sealed record HealthItem(string Component, HealthStatus Status, string Detail);

/// <summary>
/// One entry in the report's Recommendations box: an optional feature that is currently off.
/// Informational only — never a health finding, so it must not affect the report's severity.
/// </summary>
internal sealed record Recommendation(string Title, string Detail);

/// <summary>A labelled count with an optional bar fraction (0..1) for the Top-N tables.</summary>
internal sealed record NamedCount(string Name, long Count, string? SubLabel = null);

/// <summary>One day's delivered/failed counts for the daily-volume chart.</summary>
internal sealed record DailyPoint(DateOnly Date, long Sent, long Failed);

/// <summary>One message sitting in <c>mail\failed\</c> awaiting manual review.</summary>
internal sealed record FailedQueueItem(
    DateTime FailedAt, string From, string To, string Subject, string LastError, int RetryCount);

/// <summary>
/// Everything the <see cref="HtmlReportRenderer"/> needs to produce one periodic report email.
/// Assembled by <see cref="ReportDataCollector"/> from the metrics DB, the mail queue folders
/// and live health checks. All figures are plain CLR values — no HTML, no formatting.
/// </summary>
internal sealed record ReportData
{
    // ── Meta ──
    public required string Host { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }
    /// <summary>"Weekly Operations Report" / "Monthly Operations Report".</summary>
    public required string Title { get; init; }
    /// <summary>"last 7 days" / "last 30 days".</summary>
    public required string PeriodLabel { get; init; }

    // ── Queue & backlog ──
    public int QueuedNow { get; init; }
    public int FailedQueueCount { get; init; }
    public IReadOnlyList<FailedQueueItem> FailedQueueItems { get; init; } = [];

    // ── Health ──
    public IReadOnlyList<HealthItem> Health { get; init; } = [];

    /// <summary>Optional features that are switched off. Empty when everything is on — the box is then omitted.</summary>
    public IReadOnlyList<Recommendation> Recommendations { get; init; } = [];

    // ── KPIs (period vs. previous period) ──
    public long Delivered { get; init; }
    public long Failed { get; init; }
    public long PrevDelivered { get; init; }
    public long PrevFailed { get; init; }
    public double? SuccessRatePercent { get; init; }
    public double? AvgDeliveryMs { get; init; }
    public double? PeakDeliveryMs { get; init; }
    public int DistinctSenders { get; init; }
    public long VolumeBytes { get; init; }

    // ── Chart + Top-N ──
    public IReadOnlyList<DailyPoint> Daily { get; init; } = [];
    public IReadOnlyList<NamedCount> TopSenders { get; init; } = [];
    public IReadOnlyList<NamedCount> TopHosts { get; init; } = [];

    // ── System & performance ──
    public string Uptime { get; init; } = "—";
    public double? MemAvgMb { get; init; }
    public double? MemPeakMb { get; init; }
    public double? CpuAvgPct { get; init; }
    public double? CpuPeakPct { get; init; }
    public double? DiskFreePct { get; init; }

    public int WarningCount => Health.Count(h => h.Status == HealthStatus.Warning);
    public int ErrorCount => Health.Count(h => h.Status == HealthStatus.Error);
}
