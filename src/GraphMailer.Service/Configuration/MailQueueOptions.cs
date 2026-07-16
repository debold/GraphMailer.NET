namespace GraphMailer.Service.Configuration;

public sealed class MailQueueOptions
{
    public const string SectionName = "MailQueue";

    /// <summary>
    /// Base directory for the mail queue subdirectories (queue/, sent/, failed/).
    /// Leave empty (default) to use the system default: %ProgramData%\GraphMailer\mail.
    /// Can be set to a custom path (e.g. a different drive) via graphmailer.json.
    /// Override in unit tests by injecting MailQueueOptions with a temp dir.
    /// </summary>
    public string MailDir { get; init; } = string.Empty;

    public int PollingIntervalSeconds { get; init; } = 5;

    // Two-phase, time-based retry policy modelled on Microsoft Exchange and aligned with
    // RFC 5321 §4.5.4.1 (retry with increasing intervals; give up after a time budget, not a
    // fixed count). A failed message is retried quickly a few times for transient blips, then
    // at a steady interval, until MessageExpirationHours elapses since it was received — only
    // then is it moved to mail/failed/ and an NDR is sent.

    /// <summary>Number of fast initial retries for transient failures (Exchange default: 6).</summary>
    public int TransientRetryCount { get; init; } = 6;

    /// <summary>Interval between the transient retries, seconds (Exchange default: 5 min).</summary>
    public int TransientRetryIntervalSeconds { get; init; } = 300;

    /// <summary>Steady retry interval after the transient phase, seconds (Exchange default: 15 min).</summary>
    public int RetryIntervalSeconds { get; init; } = 900;

    /// <summary>
    /// How long a message is retried before it is given up (NDR + moved to failed), in hours,
    /// measured from when it was received. Exchange Online default ≈ 24 h. RFC 5321 suggests
    /// up to 4–5 days for internet MX delivery; this relay only needs to reach M365.
    /// </summary>
    public int MessageExpirationHours { get; init; } = 24;

    public int BatchSize { get; init; } = 10;
    public bool ArchiveSentEmails { get; init; } = false;
    public int SentEmailRetentionDays { get; init; } = 7;

    /// <summary>
    /// How long permanently failed messages are kept in mail/failed/ before the hourly
    /// cleanup deletes them, in days. 0 = keep forever. Default 60 — failed mail is
    /// diagnostic material, but without a bound the folder grows indefinitely.
    /// </summary>
    public int FailedEmailRetentionDays { get; init; } = 60;
}
