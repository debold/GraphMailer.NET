namespace GraphMailer.Service.Configuration;

public enum ReportFrequency
{
    Weekly,
    Monthly,
}

/// <summary>
/// Periodic (weekly/monthly) HTML operations report sent to the admin notification
/// recipients (<see cref="AdminNotificationsOptions.RecipientAddresses"/>) — no separate
/// recipient list. Disabled by default; the whole report can be switched off via
/// <see cref="Enabled"/>.
/// </summary>
public sealed class ScheduledReportOptions
{
    public bool Enabled { get; init; } = false;

    public ReportFrequency Frequency { get; init; } = ReportFrequency.Weekly;

    /// <summary>Local time of day to send, "HH:mm" (24h).</summary>
    public string TimeOfDay { get; init; } = "07:00";

    /// <summary>Day to send on when <see cref="Frequency"/> is <see cref="ReportFrequency.Weekly"/>.</summary>
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Monday;

    /// <summary>Day of month (1–28) to send on when <see cref="Frequency"/> is <see cref="ReportFrequency.Monthly"/>.</summary>
    public int DayOfMonth { get; init; } = 1;
}

public class NotificationTypeOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed class BatchedNotificationTypeOptions : NotificationTypeOptions
{
    public int BatchDelaySeconds { get; init; } = 300;
}

public sealed class ThresholdNotificationTypeOptions : NotificationTypeOptions
{
    public int FailureThreshold { get; init; } = 5;
    public int TimeWindowSeconds { get; init; } = 300;
}

public sealed class AdminNotificationTypesOptions
{
    public BatchedNotificationTypeOptions EmailDeliveryFailed { get; init; } = new();
    public NotificationTypeOptions CertificateExpiringWarning { get; init; } = new();
    public NotificationTypeOptions CertificateExpired { get; init; } = new();

    /// <summary>
    /// Advance warning before the Graph client certificate (Entra app-only auth) expires. There is
    /// deliberately no "expired" counterpart: once it lapses no Graph token can be acquired, so no
    /// email could be sent — that case is logged and shown in the operations report instead.
    /// </summary>
    public NotificationTypeOptions GraphCertificateExpiringWarning { get; init; } = new();
    public ThresholdNotificationTypeOptions AuthenticationFailureAlert { get; init; } = new();

    /// <summary>
    /// Covers both Graph API faults reported by <see cref="Services.GraphApiMonitoringService"/>:
    /// connectivity loss and missing application permissions. They share one switch because both
    /// mean the same thing operationally — Graph is not usable as configured.
    /// </summary>
    public NotificationTypeOptions GraphApiConnectionError { get; init; } = new();
    public NotificationTypeOptions LowDiskSpaceWarning { get; init; } = new();
    public ThresholdNotificationTypeOptions IpBlockedAlert { get; init; } = new() { FailureThreshold = 10 };
    public NotificationTypeOptions PortMonitoringAlert { get; init; } = new();
    public NotificationTypeOptions ConfigDecryptionError { get; init; } = new();
    public NotificationTypeOptions BackupResult { get; init; } = new();
    public NotificationTypeOptions ServiceStartStopAlert { get; init; } = new() { Enabled = false };

    /// <summary>E-mail when the weekly update check finds a newer release (opt-in, one mail per new version).</summary>
    public NotificationTypeOptions UpdateAvailable { get; init; } = new() { Enabled = false };
}

public sealed class AdminNotificationsOptions
{
    public const string SectionName = "AdminNotifications";

    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Sensitive – only set in config\graphmailer.json or env var.
    /// </summary>
    public string? SenderAddress { get; init; }
    public List<string> RecipientAddresses { get; init; } = [];
    public string SubjectPrefix { get; init; } = "[GraphMailer]";

    /// <summary>
    /// How long a <i>state-based</i> alert stays quiet before it is reported again while the
    /// condition is still present — low disk space, an expiring/expired certificate, an
    /// unreachable port, Graph connectivity or permission gaps. <c>0</c> reports each condition
    /// exactly once and stays silent until it clears.
    ///
    /// Deliberately global: every monitor is supposed to behave identically here, and a per-monitor
    /// setting is what made the cadence unpredictable before. Event-based notifications (backup
    /// result, update available, service start/stop, delivery failures) are unaffected — they
    /// report a moment in time, not a lasting state.
    /// </summary>
    public int RenotifyMinutes { get; init; } = 1440;

    /// <summary>
    /// Send a follow-up mail when a state-based alert clears (disk space recovered, certificate
    /// renewed, port reachable again, Graph connectivity or permissions restored). Only ever sent
    /// for conditions that were actually reported before, so switching an alert off cannot produce
    /// a stray "resolved" mail.
    /// </summary>
    public bool SendRecoveryNotification { get; init; } = true;

    public AdminNotificationTypesOptions NotificationTypes { get; init; } = new();

    /// <summary>Periodic operations report (weekly/monthly) sent to <see cref="RecipientAddresses"/>.</summary>
    public ScheduledReportOptions ScheduledReport { get; init; } = new();
}
