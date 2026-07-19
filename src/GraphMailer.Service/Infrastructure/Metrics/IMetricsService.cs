namespace GraphMailer.Service.Infrastructure.Metrics;

/// <summary>Aggregated email event counts for a time window (telemetry heartbeat).</summary>
internal sealed record EmailEventCounts(int Received, int Sent, int Failed);

/// <summary>
/// Records observable events to the SQLite metrics database.
/// All methods are fire-and-forget friendly: failures are logged but never thrown.
/// </summary>
internal interface IMetricsService
{
    Task RecordEmailReceivedAsync(string from, IReadOnlyList<string> toAddresses, string messageId, string subject = "", long sizeBytes = 0, int durationMs = 0, string clientIp = "", CancellationToken ct = default);
    Task RecordEmailQueuedAsync(string messageId, CancellationToken ct = default);
    Task RecordEmailSentAsync(string from, IReadOnlyList<string> toAddresses, string messageId, string subject = "", long sizeBytes = 0, int durationMs = 0, CancellationToken ct = default);
    Task RecordEmailFailedAsync(string messageId, string error, string from = "", string subject = "", CancellationToken ct = default);
    Task RecordPerfMetricAsync(string metricType, double value, CancellationToken ct = default);

    /// <summary>Counts received/sent/failed events since <paramref name="sinceUtc"/>. Returns zeros on error.</summary>
    Task<EmailEventCounts> GetEventCountsAsync(DateTime sinceUtc, CancellationToken ct = default);
}
