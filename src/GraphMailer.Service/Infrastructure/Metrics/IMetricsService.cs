namespace GraphMailer.Service.Infrastructure.Metrics;

/// <summary>A received-mail event with the full reception context (schema v2).</summary>
internal sealed record ReceivedEmailEvent
{
    public required string From { get; init; }
    public required IReadOnlyList<string> To { get; init; }
    public required string MessageId { get; init; }
    public string Subject { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int DurationMs { get; init; }
    public string ClientIp { get; init; } = string.Empty;
    public int ListenerPort { get; init; }
    public bool Tls { get; init; }
    public bool Authenticated { get; init; }
    public string AuthUser { get; init; } = string.Empty;
    /// <summary>Recipients listed in the Cc header.</summary>
    public int CcCount { get; init; }
    /// <summary>Envelope recipients that appear in neither the To nor the Cc header.</summary>
    public int BccCount { get; init; }
    public int AttachmentCount { get; init; }
    public long AttachmentBytes { get; init; }
}

/// <summary>A delivered-mail event with the delivery context (schema v2).</summary>
internal sealed record SentEmailEvent
{
    public required string From { get; init; }
    public required IReadOnlyList<string> To { get; init; }
    public required string MessageId { get; init; }
    public string Subject { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int DurationMs { get; init; }
    /// <summary>Failed attempts before this successful delivery (0 = first try).</summary>
    public int RetryCount { get; init; }
    /// <summary>Graph delivery path: "sendMail" or "draftUpload". Empty when unknown.</summary>
    public string DeliveryVariant { get; init; } = string.Empty;
    /// <summary>Milliseconds between SMTP receipt and successful Graph delivery.</summary>
    public long QueueLatencyMs { get; init; }
    public int AttachmentCount { get; init; }
    public long AttachmentBytes { get; init; }
}

/// <summary>One finished SMTP session, aggregated into hourly buckets (schema v2).</summary>
internal sealed record SmtpSessionRecord
{
    public required string ClientIp { get; init; }
    public required int ListenerPort { get; init; }
    public required SessionOutcome Outcome { get; init; }
    /// <summary>Last protocol stage the session reached (see <see cref="SessionStages"/>).</summary>
    public required string LastStage { get; init; }
    public bool Tls { get; init; }
    public bool Authenticated { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>How an SMTP session ended.</summary>
internal enum SessionOutcome
{
    /// <summary>Client said QUIT before disconnecting.</summary>
    Clean,
    /// <summary>Client disconnected without QUIT (e.g. monitoring probes).</summary>
    Aborted,
    /// <summary>Session ended with a protocol/transport exception.</summary>
    Faulted,
    /// <summary>Session was cancelled by server shutdown.</summary>
    Cancelled,
}

/// <summary>Well-known values for <see cref="SmtpSessionRecord.LastStage"/> (stored verbatim).</summary>
internal static class SessionStages
{
    public const string Connect = "connect";
    public const string Helo = "helo";
    public const string Ehlo = "ehlo";
    public const string StartTls = "starttls";
    public const string Auth = "auth";
    public const string Mail = "mail";
    public const string Rcpt = "rcpt";
    public const string Data = "data";
    public const string Quit = "quit";
}

/// <summary>Well-known rejection reasons (stored verbatim in smtp_rejection_stats).</summary>
internal static class RejectionReasons
{
    public const string IpBlacklist = "ip_blacklist";
    public const string IpNotWhitelisted = "ip_not_whitelisted";
    public const string IpBlocked = "ip_blocked";
    public const string AuthRequired = "auth_required";
    public const string AuthFailed = "auth_failed";
    public const string PriorAuthFailed = "prior_auth_failed";
    public const string FromRestriction = "from_restriction";
    public const string BlockedSender = "blocked_sender";
    public const string UnknownSender = "unknown_sender";
    public const string SenderValidationUnavailable = "sender_validation_unavailable";
    public const string BlockedRecipient = "blocked_recipient";
    public const string SizeExceeded = "size_exceeded";
    public const string QueueError = "queue_error";
}

/// <summary>
/// Aggregated counters for a time window. Consumed by the telemetry heartbeat —
/// counters only, no IPs, addresses or usernames.
/// </summary>
internal sealed record MetricsAggregates
{
    public int Received { get; init; }
    public int Sent { get; init; }
    public int Failed { get; init; }

    public int SessionsTotal { get; init; }
    public int SessionsAborted { get; init; }
    public int SessionsFaulted { get; init; }
    public int SessionsTls { get; init; }
    public int SessionsAuthenticated { get; init; }

    public int RejectionsTotal { get; init; }
    public int RejectedIp { get; init; }
    public int RejectedAuth { get; init; }
    public int RejectedSender { get; init; }
    public int RejectedRecipient { get; init; }
    public int RejectedSize { get; init; }

    public int MailsWithAttachments { get; init; }
    public int DeliveredFirstTry { get; init; }
    public int DeliveredAfterRetry { get; init; }
    public int DeliveredViaUpload { get; init; }
    public double? AvgQueueLatencyMs { get; init; }
}

/// <summary>
/// Records observable events to the SQLite metrics database.
/// All methods are fire-and-forget friendly: failures are logged but never thrown.
/// </summary>
internal interface IMetricsService
{
    Task RecordEmailReceivedAsync(ReceivedEmailEvent e, CancellationToken ct = default);
    Task RecordEmailQueuedAsync(string messageId, CancellationToken ct = default);
    Task RecordEmailSentAsync(SentEmailEvent e, CancellationToken ct = default);
    Task RecordEmailFailedAsync(string messageId, string error, string from = "", string subject = "", int retryCount = 0, bool permanent = false, CancellationToken ct = default);
    Task RecordPerfMetricAsync(string metricType, double value, CancellationToken ct = default);

    /// <summary>Counts one finished SMTP session into its hourly aggregate bucket.</summary>
    Task RecordSmtpSessionAsync(SmtpSessionRecord r, CancellationToken ct = default);

    /// <summary>Counts one rejected command/connection into its hourly aggregate bucket.</summary>
    Task RecordRejectionAsync(string reason, string clientIp, int listenerPort, CancellationToken ct = default);

    /// <summary>Aggregated counters since <paramref name="sinceUtc"/>. Returns zeros on error.</summary>
    Task<MetricsAggregates> GetAggregatesAsync(DateTime sinceUtc, CancellationToken ct = default);
}
