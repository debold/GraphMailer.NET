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
    public ThresholdNotificationTypeOptions AuthenticationFailureAlert { get; init; } = new();
    public NotificationTypeOptions GraphApiConnectionError { get; init; } = new();
    public NotificationTypeOptions QueueProcessorFailure { get; init; } = new();
    public NotificationTypeOptions LowDiskSpaceWarning { get; init; } = new();
    public ThresholdNotificationTypeOptions IpBlockedAlert { get; init; } = new() { FailureThreshold = 10 };
    public NotificationTypeOptions PortMonitoringAlert { get; init; } = new();
    public NotificationTypeOptions PortMonitoringRecovery { get; init; } = new();
    public NotificationTypeOptions PortMonitoringSustainedOutage { get; init; } = new();
    public NotificationTypeOptions GraphApiConnectivityRestored { get; init; } = new();
    public NotificationTypeOptions ConfigDecryptionError { get; init; } = new();
    public NotificationTypeOptions BackupResult { get; init; } = new();
    public NotificationTypeOptions ServiceStartStopAlert { get; init; } = new() { Enabled = false };
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
    public AdminNotificationTypesOptions NotificationTypes { get; init; } = new();

    /// <summary>Periodic operations report (weekly/monthly) sent to <see cref="RecipientAddresses"/>.</summary>
    public ScheduledReportOptions ScheduledReport { get; init; } = new();
}
